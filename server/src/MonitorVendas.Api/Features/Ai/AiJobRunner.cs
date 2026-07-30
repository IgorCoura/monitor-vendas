using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.ReportExport;
using MonitorVendas.Api.Integrations.Ai;

namespace MonitorVendas.Api.Features.Ai;

public sealed record AiJobFilters(
    DateTime From,
    DateTime To,
    IReadOnlyList<Guid> SellerIds,
    IReadOnlyList<Guid> ConversationIds);

public interface IAiJobRunner
{
    Task<int> ProcessPendingAsync(CancellationToken ct = default);
}

// Executa os dois botões da tela de análises. Reusa o mesmo workset, o mesmo
// analisador e o mesmo sintetizador da exportação — a leitura de uma conversa é
// a mesma, tenha vindo de onde vier.
public sealed class AiJobRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<ReportExportOptions> exportOptions,
    IOptions<AiOptions> aiOptions,
    ILogger<AiJobRunner> logger) : IAiJobRunner
{
    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        var processed = 0;
        var visited = new HashSet<Guid>();

        while (!ct.IsCancellationRequested)
        {
            Guid jobId;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var next = await db.Set<AiJob>().AsNoTracking()
                    .Where(j => j.Status == AiJobStatus.Pending && !visited.Contains(j.Id))
                    .OrderBy(j => j.CreatedAt)
                    .Select(j => (Guid?)j.Id)
                    .FirstOrDefaultAsync(ct);

                if (next is null)
                    break;

                jobId = next.Value;
            }

            visited.Add(jobId);
            await RunAsync(jobId, ct);
            processed++;
        }

        return processed;
    }

    private async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.Set<AiJob>().FirstAsync(j => j.Id == jobId, ct);
        var filters = JsonSerializer.Deserialize<AiJobFilters>(job.FiltersJson)
            ?? new AiJobFilters(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, [], []);

        job.Status = AiJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, exportOptions.Value.AiDeadlineSeconds));

            if (job.Kind == AiJobKind.Analyze)
                await AnalyzeAsync(db, job, filters, deadline, ct);
            else
                await SynthesizeAsync(db, job, filters, deadline, ct);

            job.Status = AiJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha no job de IA {JobId}.", jobId);
            job.Status = AiJobStatus.Failed;
            job.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task AnalyzeAsync(AppDbContext db, AiJob job, AiJobFilters filters, DateTime deadline, CancellationToken ct)
    {
        var (conversations, catalog) = await LoadAsync(filters, force: true, ct);

        // Seleção explícita da tela ganha do período: o usuário marcou aquelas.
        if (filters.ConversationIds.Count > 0)
        {
            var wanted = filters.ConversationIds.ToHashSet();
            conversations = [.. conversations.Where(c => wanted.Contains(c.ConversationId))];
        }

        job.Total = conversations.Count;
        await db.SaveChangesAsync(ct);

        foreach (var chunk in conversations.Chunk(Math.Max(1, aiOptions.Value.MaxConcurrency)))
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline)
                break;

            var results = await Task.WhenAll(chunk.Select(async conversation =>
            {
                using var inner = scopeFactory.CreateScope();
                var analyzer = inner.ServiceProvider.GetRequiredService<ConversationAnalyzer>();
                return await analyzer.AnalyzeAsync(conversation.Input, catalog, ct);
            }));

            foreach (var outcome in results)
            {
                if (outcome.Kind == AnalysisResultKind.Analyzed)
                {
                    job.Processed++;
                    job.CostBrl += outcome.Analysis?.CostBrl ?? 0m;
                }
                else
                {
                    job.Skipped++;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        job.Skipped += conversations.Count - job.Processed - job.Skipped;
    }

    private async Task SynthesizeAsync(AppDbContext db, AiJob job, AiJobFilters filters, DateTime deadline, CancellationToken ct)
    {
        var (conversations, _) = await LoadAsync(filters, force: false, ct);
        var ids = conversations.Select(c => c.ConversationId).ToList();

        var analyses = await db.Set<ConversationAiAnalysis>().AsNoTracking()
            .Where(a => a.IsCurrent && ids.Contains(a.ConversationId))
            .ToDictionaryAsync(a => a.ConversationId, ct);

        var typeNames = await db.Set<Outcomes.ConversationOutcomeType>().AsNoTracking()
            .ToDictionaryAsync(t => t.Code, t => t.Name, ct);

        var groups = conversations
            .Where(c => c.SellerId is not null && analyses.ContainsKey(c.ConversationId))
            .GroupBy(c => c.SellerId!.Value)
            .ToList();

        job.Total = groups.Count;
        await db.SaveChangesAsync(ct);

        foreach (var group in groups)
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline)
                break;

            var rows = group
                .Select(c => AiRowMapper.ToRow(c, analyses.GetValueOrDefault(c.ConversationId), typeNames, null))
                .ToList();

            using var inner = scopeFactory.CreateScope();
            var synthesizer = inner.ServiceProvider.GetRequiredService<SellerSynthesizer>();

            // Force: o botão existe justamente para refazer o que já está em cache.
            var synthesis = await synthesizer.SynthesizeAsync(
                ReportExportRunner.BuildSynthesisInput(group.Key, rows), force: true, ct);

            if (synthesis.Error is null)
                job.Processed++;
            else
                job.Skipped++;

            job.CostBrl += synthesis.CostBrl;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<(List<ConversationContext> Conversations, List<OutcomeChoice> Catalog)> LoadAsync(
        AiJobFilters filters,
        bool force,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var workset = scope.ServiceProvider.GetRequiredService<ConversationAiWorkset>();

        var (items, _) = await workset.LoadAsync(
            new ConversationAiFilter(filters.From, filters.To, filters.SellerIds,
                exportOptions.Value.MaxConversationsPerExport, force),
            ct);

        return (items, await workset.CatalogAsync(ct));
    }
}

public sealed class AiJobBackgroundService(
    IAiJobRunner runner,
    IOptions<ReportExportOptions> options,
    ILogger<AiJobBackgroundService> logger) : BackgroundService
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
                logger.LogError(ex, "Erro no loop de jobs de IA.");
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
