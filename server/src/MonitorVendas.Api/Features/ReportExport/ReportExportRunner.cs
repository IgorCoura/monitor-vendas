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

    private sealed record ConversationRow(
        Guid Id,
        Guid NumberId,
        Guid ContactId,
        bool StartedByContact,
        DateTime StartedAt,
        DateTime LastMessageAt);

    private sealed record NumberRow(Guid Id, string Phone, Guid SellerId, string SellerName);

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
        var (conversations, _) = await LoadConversationsAsync(db, request, ct);
        if (conversations.Count == 0)
            return new AiResult([], [], new AiExportSummary(aiOptions.Value.Model, 0, 0, 0, 0m, DateTime.UtcNow));

        var conversationIds = conversations.Select(c => c.Id).ToList();
        var numbers = (await NumbersAsync(db, request, ct)).ToDictionary(n => n.Id);
        var contacts = await db.Set<Contact>().AsNoTracking()
            .Where(c => conversations.Select(x => x.ContactId).Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        var typeNames = await db.Set<ConversationOutcomeType>().AsNoTracking()
            .ToDictionaryAsync(t => t.Code, t => t.Name, ct);
        var catalog = await db.Set<ConversationOutcomeType>().AsNoTracking()
            .Where(t => t.Active)
            .OrderBy(t => t.SortOrder)
            .Select(t => new OutcomeChoice(t.Code, t.Name))
            .ToListAsync(ct);

        var outcomes = await db.Set<ConversationOutcome>().AsNoTracking()
            .Where(o => conversationIds.Contains(o.ConversationId))
            .ToDictionaryAsync(o => o.ConversationId, o => o.OutcomeTypeCode, ct);

        var messages = await db.Set<Message>().AsNoTracking()
            .Where(m => conversationIds.Contains(m.ConversationId))
            .OrderBy(m => m.Timestamp)
            .Select(m => new TranscriptRow(m.ConversationId, m.Direction, m.Timestamp, m.Text, m.Type))
            .ToListAsync(ct);

        var byConversation = messages.GroupBy(m => m.ConversationId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var calendar = await queries.BuildCalendarAsync(ct);
        var now = DateTime.UtcNow;
        var staleAfter = metricsOptions.Value.FollowUpGapBusinessHours;

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
                var rows = byConversation.TryGetValue(conversation.Id, out var list) ? list : [];
                var contact = contacts.GetValueOrDefault(conversation.ContactId);
                var silence = calendar.BusinessTimeBetween(conversation.LastMessageAt, now).TotalHours;

                var transcript = TranscriptBuilder.Build(
                    [.. rows.Select(r => new TranscriptMessage(r.Direction, r.Timestamp, r.Text, r.Type))],
                    contact?.PushName,
                    PhoneOf(contact?.RemoteJid),
                    TimeZoneInfo.FindSystemTimeZoneById(metricsOptions.Value.TimeZone),
                    conversation.StartedByContact,
                    silence);

                var input = new ConversationAnalysisInput(
                    conversation.Id, rows.Count, conversation.LastMessageAt, transcript, silence <= staleAfter);

                using var scope = scopeFactory.CreateScope();
                var analyzer = scope.ServiceProvider.GetRequiredService<ConversationAnalyzer>();
                return (conversation.Id, Outcome: await analyzer.AnalyzeAsync(input, catalog, ct));
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
            var number = numbers.GetValueOrDefault(conversation.NumberId);
            var contact = contacts.GetValueOrDefault(conversation.ContactId);
            var realCode = outcomes.GetValueOrDefault(conversation.Id);
            analyses.TryGetValue(conversation.Id, out var outcome);
            var analysis = outcome?.Analysis;

            var aiCode = analysis?.StatusCode == ConversationAiAnalysis.Open ? null : analysis?.StatusCode;

            return new AiConversationRow(
                number?.SellerName ?? "—",
                number?.Phone ?? "—",
                contact?.PushName ?? PhoneOf(contact?.RemoteJid) ?? "—",
                PhoneOf(contact?.RemoteJid) ?? "—",
                conversation.StartedAt,
                conversation.LastMessageAt,
                realCode is null ? null : typeNames.GetValueOrDefault(realCode, realCode),
                analysis is null ? null
                    : aiCode is null ? "Em andamento" : typeNames.GetValueOrDefault(aiCode, aiCode),
                analysis?.StatusConfidence,
                analysis is not null && !string.Equals(aiCode, realCode, StringComparison.OrdinalIgnoreCase),
                analysis?.StatusEvidence,
                analysis?.LossReason,
                analysis?.AskedForSale,
                analysis?.IgnoredBuyingSignal,
                analysis?.Objections,
                analysis?.ShouldRecontact,
                analysis?.RecontactReason,
                analysis?.SuggestedMessage,
                analysis?.Interest,
                analysis?.Summary,
                analysis?.ConductAlert,
                analysis is not null ? null : outcome?.Error ?? skippedReason ?? NoBudgetReason);
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

        foreach (var group in rows.Where(r => r.Summary is not null).GroupBy(r => r.SellerName))
        {
            // Cada síntese pode esperar cota; passado o prazo, o resto sai sem ela.
            if (DateTime.UtcNow >= deadline)
                break;

            using var scope = scopeFactory.CreateScope();
            var synthesizer = scope.ServiceProvider.GetRequiredService<SellerSynthesizer>();

            var lines = group.Select(r =>
                $"{r.AiStatus ?? "—"} | {AiSheetsWriter.FriendlyLossReason(r.LossReason) ?? "—"} | {r.Summary}").ToList();
            var summary = string.Join('\n', new[]
            {
                $"Conversas auditadas: {lines.Count}",
                $"Pediu a venda em: {group.Count(r => r.AskedForSale == true)}",
                $"Sinais de compra ignorados: {group.Count(r => r.IgnoredBuyingSignal == true)}",
            });

            var input = new SellerSynthesisInput(Guid.Empty, group.Key, summary, lines);
            syntheses.Add(await synthesizer.SynthesizeAsync(input, ct));
        }

        return syntheses;
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

        var (conversations, truncated) = await LoadConversationsAsync(db, request, ct);
        var ids = conversations.Select(c => c.Id).ToList();

        var sizes = await db.Set<Message>().AsNoTracking()
            .Where(m => ids.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => new { ConversationId = g.Key, Count = g.Count(), Chars = g.Sum(m => (m.Text ?? "").Length) })
            .ToDictionaryAsync(x => x.ConversationId, ct);

        var done = await db.Set<ConversationAiAnalysis>().AsNoTracking()
            .Where(a => ids.Contains(a.ConversationId))
            .Select(a => new { a.ConversationId, a.MessageCount, a.LastMessageAt })
            .ToListAsync(ct);

        var cachedIds = done
            .Where(a => sizes.TryGetValue(a.ConversationId, out var size) && size.Count == a.MessageCount &&
                        conversations.Any(c => c.Id == a.ConversationId && c.LastMessageAt == a.LastMessageAt))
            .Select(a => a.ConversationId)
            .ToHashSet();

        var settings = aiOptions.Value;
        var estimate = 0m;
        foreach (var conversation in conversations.Where(c => !cachedIds.Contains(c.Id)))
        {
            // O prompt é a transcrição mais o cabeçalho de instruções — sem montar
            // a transcrição de verdade, que custaria a exportação inteira só para
            // mostrar um preço na tela.
            var chars = sizes.GetValueOrDefault(conversation.Id)?.Chars ?? 0;
            var prompt = new string('x', chars + AiAnalysisSchema.SystemPrompt.Length + 600);
            estimate += calculator.EstimateBrl(settings.Model, prompt, settings.MaxOutputTokens, budget.MarginPercent);
        }

        var sellers = (await NumbersAsync(db, request, ct)).Select(n => n.SellerId).Distinct().Count();
        estimate += sellers * calculator.EstimateBrl(settings.Model, new string('x', 4_000), 700, budget.MarginPercent);

        var toAnalyze = conversations.Count - cachedIds.Count;
        return new ReportExportEstimate(
            conversations.Count, cachedIds.Count, toAnalyze, estimate,
            status.Available, !status.Enabled || estimate <= status.Available, truncated);
    }

    private sealed record TranscriptRow(Guid ConversationId, MessageDirection Direction, DateTime Timestamp, string? Text, string Type);

    private async Task<(List<ConversationRow> Conversations, bool Truncated)> LoadConversationsAsync(
        AppDbContext db,
        ReportExportRequest request,
        CancellationToken ct)
    {
        var numberIds = (await NumbersAsync(db, request, ct)).Select(n => n.Id).ToList();
        var max = Math.Max(1, options.Value.MaxConversationsPerExport);

        var conversations = await db.Set<Conversation>().AsNoTracking()
            .Where(c => numberIds.Contains(c.WhatsappNumberId) &&
                        c.LastMessageAt >= request.From && c.StartedAt <= request.To)
            .OrderByDescending(c => c.LastMessageAt)
            .Take(max + 1)
            .Select(c => new ConversationRow(c.Id, c.WhatsappNumberId, c.ContactId, c.StartedByContact, c.StartedAt, c.LastMessageAt))
            .ToListAsync(ct);

        var truncated = conversations.Count > max;
        if (truncated)
            conversations.RemoveAt(conversations.Count - 1);

        return (conversations, truncated);
    }

    private static async Task<List<NumberRow>> NumbersAsync(AppDbContext db, ReportExportRequest request, CancellationToken ct)
    {
        var numbers = db.Set<WhatsappNumber>().AsNoTracking();

        // O filtro tem que vir antes da projeção: sobre o NumberRow já montado, o
        // EF não traduz o Contains e a exportação estoura em 500.
        if (request.SellerIds.Count > 0)
        {
            var sellerIds = request.SellerIds.ToList();
            numbers = numbers.Where(n => sellerIds.Contains(n.SellerId));
        }

        return await numbers
            .Join(db.Set<Seller>().AsNoTracking(), n => n.SellerId, s => s.Id,
                (n, s) => new NumberRow(n.Id, n.Phone, s.Id, s.Name))
            .ToListAsync(ct);
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

    private static string? PhoneOf(string? remoteJid) =>
        remoteJid is null ? null : new string([.. remoteJid.TakeWhile(c => c is not '@' and not ':').Where(char.IsDigit)]);

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
