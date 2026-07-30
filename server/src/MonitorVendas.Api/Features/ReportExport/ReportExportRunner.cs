using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Ai;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Integrations.Ai;

namespace MonitorVendas.Api.Features.ReportExport;

public sealed record ReportExportEstimate(
    int Conversations,
    int Cached,
    int ToAnalyze,
    decimal EstimatedBrl,
    decimal Available,
    bool Affordable,
    bool Truncated);

public interface IReportExportRunner
{
    Task<int> ProcessPendingAsync(CancellationToken ct = default);

    Task<ReportExportEstimate> EstimateAsync(ReportExportRequest request, CancellationToken ct = default);
}

public sealed class ReportExportRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<ReportExportOptions> options,
    IOptions<MetricsOptions> metricsOptions,
    IOptions<AiOptions> aiOptions,
    ILogger<ReportExportRunner> logger) : IReportExportRunner
{
    private const string NoBudgetReason = "Saldo de IA insuficiente.";
    private const string TimeoutReason = "Prazo da análise por IA esgotado (cota do provedor).";

    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        var processed = 0;
        var visited = new HashSet<Guid>();
        var concurrency = Math.Max(1, options.Value.MaxConcurrentExports);

        while (!ct.IsCancellationRequested)
        {
            List<Guid> batch;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                batch = await db.Set<ReportExport>().AsNoTracking()
                    .Where(e => e.Status == ReportExportStatus.Pending && !visited.Contains(e.Id))
                    .OrderBy(e => e.CreatedAt)
                    .Select(e => e.Id)
                    .Take(concurrency)
                    .ToListAsync(ct);
            }

            if (batch.Count == 0)
                break;

            foreach (var id in batch)
                visited.Add(id);

            // Em paralelo de propósito: uma exportação presa em limite de cota não
            // pode segurar as outras, que costumam terminar em milissegundos.
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                async (id, token) => await RunAsync(id, token));

            processed += batch.Count;
        }

        await PurgeExpiredAsync(ct);

        return processed;
    }

    private async Task RunAsync(Guid exportId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queries = scope.ServiceProvider.GetRequiredService<ReportQueries>();

        var export = await db.Set<ReportExport>().FirstAsync(e => e.Id == exportId, ct);
        var request = JsonSerializer.Deserialize<ReportExportRequest>(export.FiltersJson)
            ?? ReportExportRequest.Empty(export.From, export.To);

        export.Status = ReportExportStatus.Running;
        export.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(metricsOptions.Value.TimeZone);

            var ranking = await queries.GetRankingAsync(export.From, export.To, ct);
            if (request.SellerIds.Count > 0)
            {
                var wanted = request.SellerIds.ToHashSet();
                ranking = [.. ranking.Where(r => wanted.Contains(r.SellerId))];
            }

            var sellerReports = new List<SellerReportDto>();
            if (request.IncludeNumbers)
            {
                foreach (var entry in ranking)
                {
                    var report = await queries.GetSellerReportAsync(entry.SellerId, export.From, export.To, ct);
                    if (report is not null)
                        sellerReports.Add(report);
                }
            }

            var ai = request.IncludeAi
                ? await BuildAiAsync(db, queries, export, request, ct)
                : new AiResult([], [], null);

            var data = new ReportExportData(export.From, export.To, ranking, sellerReports, ai.Rows, ai.Syntheses, ai.Summary);
            var bytes = ReportWorkbookWriter.Build(data, request, timeZone);

            export.File = bytes;
            export.FileName = FileNameFor(export.From, export.To, timeZone);
            export.Phase = null;
            export.Status = ReportExportStatus.Completed;
            export.Error = null;
            export.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha ao gerar a exportação {ExportId}.", exportId);
            export.Phase = null;
            export.Status = ReportExportStatus.Failed;
            export.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            export.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private sealed record AiResult(
        IReadOnlyList<AiConversationRow> Rows,
        IReadOnlyList<SellerSynthesis> Syntheses,
        AiExportSummary? Summary);

    private async Task<AiResult> BuildAiAsync(
        AppDbContext db,
        ReportQueries queries,
        ReportExport export,
        ReportExportRequest request,
        CancellationToken ct)
    {
        var (conversations, _) = await LoadWorksetAsync(request, ct);

        if (conversations.Count == 0)
            return new AiResult([], [], new AiExportSummary(aiOptions.Value.Model, 0, 0, 0, 0m, DateTime.UtcNow));

        var (typeNames, catalog) = await CatalogAsync(ct);
        var analyses = new ConcurrentDictionary<Guid, AnalysisOutcome>();
        var noBudget = false;
        var analyzed = 0;
        var cached = 0;
        var cost = 0m;

        // A fase de IA tem prazo: o relatório já está pronto e não pode ficar
        // preso esperando a cota do provedor liberar.
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, options.Value.AiDeadlineSeconds));
        var timedOut = false;

        export.Phase = "Analisando conversas";
        await db.SaveChangesAsync(ct);

        foreach (var chunk in conversations.Chunk(Math.Max(1, aiOptions.Value.MaxConcurrency)))
        {
            if (noBudget || ct.IsCancellationRequested)
                break;

            if (DateTime.UtcNow >= deadline)
            {
                timedOut = true;
                break;
            }

            var results = await Task.WhenAll(chunk.Select(async conversation =>
            {
                using var scope = scopeFactory.CreateScope();
                var analyzer = scope.ServiceProvider.GetRequiredService<ConversationAnalyzer>();
                return (conversation.ConversationId, Outcome: await analyzer.AnalyzeAsync(conversation.Input, catalog, ct));
            }));

            foreach (var (id, outcome) in results)
            {
                analyses[id] = outcome;
                switch (outcome.Kind)
                {
                    case AnalysisResultKind.Analyzed:
                        analyzed++;
                        cost += outcome.Analysis?.CostBrl ?? 0m;
                        break;
                    case AnalysisResultKind.Cached:
                        cached++;
                        break;
                    case AnalysisResultKind.NoBudget:
                        noBudget = true;
                        break;
                }
            }

            export.AnalyzedConversations = analyzed;
            export.CachedConversations = cached;
            export.TotalConversations = conversations.Count;
            export.CostBrl = cost;
            await db.SaveChangesAsync(ct);
        }

        var skippedReason = noBudget ? NoBudgetReason : timedOut ? TimeoutReason : null;

        var rowsOut = conversations.Select(conversation =>
        {
            analyses.TryGetValue(conversation.ConversationId, out var outcome);

            return AiRowMapper.ToRow(
                conversation,
                outcome?.Analysis,
                typeNames,
                outcome?.Error ?? skippedReason ?? NoBudgetReason);
        }).ToList();

        export.Phase = "Sintetizando vendedores";
        await db.SaveChangesAsync(ct);

        var syntheses = await SynthesizeAsync(rowsOut, noBudget || timedOut, deadline, ct);
        cost += syntheses.Sum(s => s.CostBrl);

        var skipped = rowsOut.Count(r => r.NotAnalyzedReason is not null);
        export.SkippedConversations = skipped;
        export.CostBrl = cost;
        await db.SaveChangesAsync(ct);

        return new AiResult(rowsOut, syntheses,
            new AiExportSummary(aiOptions.Value.Model, analyzed, cached, skipped, cost, DateTime.UtcNow));
    }

    // A síntese roda sobre os resumos já prontos: uma chamada por vendedor.
    private async Task<List<SellerSynthesis>> SynthesizeAsync(
        IReadOnlyList<AiConversationRow> rows,
        bool skip,
        DateTime deadline,
        CancellationToken ct)
    {
        if (skip)
            return [];

        var syntheses = new List<SellerSynthesis>();

        foreach (var group in rows.Where(r => r.Summary is not null && r.SellerId is not null).GroupBy(r => r.SellerId!.Value))
        {
            // Cada síntese pode esperar cota; passado o prazo, o resto sai sem ela.
            // O cache não espera nada, então só o que vai chamar a IA é cortado.
            if (DateTime.UtcNow >= deadline)
                break;

            using var scope = scopeFactory.CreateScope();
            var synthesizer = scope.ServiceProvider.GetRequiredService<SellerSynthesizer>();
            syntheses.Add(await synthesizer.SynthesizeAsync(BuildSynthesisInput(group.Key, [.. group]), ct: ct));
        }

        return syntheses;
    }

    // O carregamento das conversas mora no ConversationAiWorkset: a tela de
    // análises usa exatamente o mesmo, e duplicar era garantir divergência.
    private async Task<(List<ConversationContext> Items, bool Truncated)> LoadWorksetAsync(
        ReportExportRequest request,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var workset = scope.ServiceProvider.GetRequiredService<ConversationAiWorkset>();

        return await workset.LoadAsync(
            new ConversationAiFilter(request.From, request.To, request.SellerIds, options.Value.MaxConversationsPerExport, Force: false, IncludeAudio: request.IncludeAudio),
            ct);
    }

    private async Task<(Dictionary<string, string> TypeNames, List<OutcomeChoice> Catalog)> CatalogAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var workset = scope.ServiceProvider.GetRequiredService<ConversationAiWorkset>();

        return (await workset.TypeNamesAsync(ct), await workset.CatalogAsync(ct));
    }

    // As linhas que alimentam a síntese também definem a chave do cache dela.
    internal static SellerSynthesisInput BuildSynthesisInput(Guid sellerId, IReadOnlyList<AiConversationRow> rows)
    {
        var lines = rows
            .Select(r => $"{r.AiStatus ?? "—"} | {AiSheetsWriter.FriendlyLossReason(r.LossReason) ?? "—"} | {r.Summary}")
            .ToList();

        var summary = string.Join('\n', new[]
        {
            $"Conversas auditadas: {lines.Count}",
            $"Pediu a venda em: {rows.Count(r => r.AskedForSale == true)}",
            $"Sinais de compra ignorados: {rows.Count(r => r.IgnoredBuyingSignal == true)}",
        });

        var analyses = rows
            .Where(r => r.AnalysisId is not null && r.AnalyzedAt is not null)
            .Select(r => new AnalysisRef(r.AnalysisId!.Value, r.AnalyzedAt!.Value))
            .ToList();

        return new SellerSynthesisInput(sellerId, rows[0].SellerName, summary, lines, analyses);
    }

    public async Task<ReportExportEstimate> EstimateAsync(ReportExportRequest request, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var budget = scope.ServiceProvider.GetRequiredService<AiBudget>();
        var calculator = scope.ServiceProvider.GetRequiredService<AiCostCalculator>();

        var status = await budget.GetStatusAsync(ct);
        if (!request.IncludeAi)
            return new ReportExportEstimate(0, 0, 0, 0m, status.Available, true, false);

        var (conversations, truncated) = await LoadWorksetAsync(request, ct);
        var ids = conversations.Select(c => c.ConversationId).ToList();

        var done = await db.Set<ConversationAiAnalysis>().AsNoTracking()
            .Where(a => ids.Contains(a.ConversationId) && a.IsCurrent)
            .Select(a => new { a.ConversationId, a.MessageCount, a.LastMessageAt, a.IncludedAudio })
            .ToListAsync(ct);

        var cachedIds = conversations
            .Where(c => done.Any(a => a.ConversationId == c.ConversationId &&
                                      a.MessageCount == c.Input.MessageCount &&
                                      a.LastMessageAt == c.LastMessageAt &&
                                      a.IncludedAudio == (c.Input.Attachments is { Count: > 0 })))
            .Select(c => c.ConversationId)
            .ToHashSet();

        var settings = aiOptions.Value;
        var estimate = 0m;

        // A transcrição já vem montada pelo workset, então a estimativa usa o
        // prompt de verdade em vez de um palpite pelo tamanho das mensagens.
        foreach (var conversation in conversations.Where(c => !cachedIds.Contains(c.ConversationId)))
        {
            var prompt = AiAnalysisSchema.SystemPrompt + conversation.Input.Transcript;
            estimate += calculator.EstimateBrl(
                settings.Model, prompt, settings.MaxOutputTokens, budget.MarginPercent, conversation.AudioSeconds);
        }

        // A síntese só custa quando o conjunto de leituras do vendedor muda.
        var sellersToSynthesize = conversations
            .Where(c => c.SellerId is not null && !cachedIds.Contains(c.ConversationId))
            .Select(c => c.SellerId)
            .Distinct()
            .Count();
        estimate += sellersToSynthesize *
            calculator.EstimateBrl(settings.Model, new string('x', 4_000), settings.MaxOutputTokens, budget.MarginPercent);

        var toAnalyze = conversations.Count - cachedIds.Count;
        return new ReportExportEstimate(
            conversations.Count, cachedIds.Count, toAnalyze, estimate,
            status.Available, !status.Enabled || estimate <= status.Available, truncated);
    }

    private async Task PurgeExpiredAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, options.Value.RetentionHours));

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Set<ReportExport>()
            .Where(e => e.CompletedAt != null && e.CompletedAt < cutoff && e.File != null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.File, (byte[]?)null), ct);
    }

    internal static string FileNameFor(DateTime from, DateTime to, TimeZoneInfo timeZone) =>
        $"relatorio-{ReportWorkbookWriter.Local(from, timeZone):yyyy-MM-dd}-a-{ReportWorkbookWriter.Local(to, timeZone):yyyy-MM-dd}.xlsx";
}

public sealed class ReportExportBackgroundService(
    IReportExportRunner runner,
    IOptions<ReportExportOptions> options,
    ILogger<ReportExportBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.IntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no loop de exportação de relatórios.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
