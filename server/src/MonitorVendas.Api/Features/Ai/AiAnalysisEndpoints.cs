using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Common;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Ai.Export;
using MonitorVendas.Api.Features.Metrics;
using Npgsql;

namespace MonitorVendas.Api.Features.Ai;

public sealed record AiAnalysisRowDto(
    Guid ConversationId,
    Guid AnalysisId,
    Guid? SellerId,
    string SellerName,
    string SellerNumber,
    string ContactName,
    string ContactPhone,
    DateTime StartedAt,
    DateTime LastMessageAt,
    string? RealOutcome,
    string? AiStatus,
    string? AiStatusCode,
    double Confidence,
    bool Divergent,
    string? Evidence,
    string? LossReason,
    bool AskedForSale,
    bool IgnoredBuyingSignal,
    string? Objections,
    bool ShouldRecontact,
    string? RecontactReason,
    string? SuggestedMessage,
    string? Interest,
    string? Summary,
    string? ConductAlert,
    string Model,
    DateTime AnalyzedAt,
    int Versions,
    // Áudios da conversa e quantos o modelo ouviu. Leitura surda e leitura
    // completa precisam ser distinguíveis na tela.
    int AudioExpected,
    int AudioAttached);

public sealed record AiAnalysisPageDto(IReadOnlyList<AiAnalysisRowDto> Items, int Page, int PageSize, int Total);

public sealed record AiSynthesisDto(
    Guid SellerId,
    string SellerName,
    string? Overview,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Improvements,
    string? DominantLossPattern,
    string? TrainingSuggestion,
    int ConversationsCount,
    string Model,
    DateTime CreatedAt,
    bool Stale);

public sealed record AiJobDto(
    Guid Id,
    string Kind,
    string Status,
    int Total,
    int Processed,
    int Skipped,
    decimal CostBrl,
    string? Error,
    DateTime CreatedAt,
    DateTime? CompletedAt)
{
    public static AiJobDto Of(AiJob job) =>
        new(job.Id, job.Kind.ToString(), job.Status.ToString(), job.Total, job.Processed,
            job.Skipped, job.CostBrl, job.Error, job.CreatedAt, job.CompletedAt);
}

public sealed record AiRunRequest(
    DateTime? From,
    DateTime? To,
    IReadOnlyList<Guid>? SellerIds,
    IReadOnlyList<Guid>? ConversationIds,
    bool? IncludeAudio,
    // Ausente = só refaz o que mudou, que é o que a tela pede. `true` ignora o
    // cache e reprocessa tudo — sem botão, mas disponível para reprocessar à mão
    // quando o prompt ou o modelo mudam.
    bool? Force);

public sealed record AiEstimateRequest(
    string? Kind,
    DateTime? From,
    DateTime? To,
    IReadOnlyList<Guid>? SellerIds,
    IReadOnlyList<Guid>? ConversationIds,
    bool? IncludeAudio,
    bool? Force);

// O que a tela precisa saber para decidir se mostra os botões: se há rodada em
// andamento e quando cada tipo terminou pela última vez. Vem do banco, então
// sobrevive a recarregar a página.
public sealed record AiStatusDto(AiJobDto? Running, AiJobDto? LastAnalysis, AiJobDto? LastSynthesis);

public static class AiAnalysisEndpoints
{
    public static RouteGroupBuilder MapAiAnalysisEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/ai/analyses", async (
            DateTime? from,
            DateTime? to,
            Guid? sellerId,
            string? status,
            string? lossReason,
            bool? divergent,
            bool? recontact,
            int? page,
            int? pageSize,
            AiAnalysisQueries queries,
            CancellationToken ct) =>
        {
            var take = Math.Clamp(pageSize ?? 50, 1, 200);
            var current = Math.Max(1, page ?? 1);

            var (items, total) = await queries.ListAsync(
                Filter(from, to, sellerId, status, lossReason, divergent, recontact), current, take, ct);

            return Results.Ok(new AiAnalysisPageDto(items, current, take, total));
        });

        // Mesma lista da tela, sem paginação, embrulhada em planilha. Nenhuma
        // chamada de IA acontece aqui: exporta o que já foi lido.
        group.MapGet("/ai/analyses/export", async (
            DateTime? from,
            DateTime? to,
            Guid? sellerId,
            string? status,
            string? lossReason,
            bool? divergent,
            bool? recontact,
            HttpResponse response,
            AiAnalysisQueries queries,
            IOptions<MetricsOptions> metrics,
            CancellationToken ct) =>
        {
            var filter = Filter(from, to, sellerId, status, lossReason, divergent, recontact);
            var (items, total) = await queries.ListAsync(filter, ct: ct);

            if (total > items.Count)
                response.Headers["X-Truncated"] = "true";

            var syntheses = await queries.SynthesesAsync(sellerId, ct);
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(metrics.Value.TimeZone);
            var file = AiAnalysisWorkbookWriter.Build(items, syntheses, timeZone);

            return Results.File(file, AiAnalysisWorkbookWriter.ContentType, FileNameFor(filter, timeZone));
        });

        group.MapGet("/ai/syntheses", async (Guid? sellerId, AiAnalysisQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.SynthesesAsync(sellerId, ct)));

        // A taxonomia de perda é fechada e definida no schema do prompt: a tela lê
        // daqui em vez de repetir a lista e sair de sincronia.
        group.MapGet("/ai/loss-reasons", () => Results.Ok(
            AiAnalysisSchema.LossReasons.Select(code => new
            {
                Code = code,
                Label = AiAnalysisSchema.FriendlyLossReason(code),
            })));

        // Quanto a rodada vai custar, antes de confirmar. Mesma conta que o
        // endpoint de criação e o runner usam para recusar.
        group.MapPost("/ai/estimate", async (
            AiEstimateRequest body,
            AiJobEstimator estimator,
            CancellationToken ct) =>
        {
            var kind = KindOf(body.Kind);
            var filters = FiltersOf(
                body.From, body.To, body.SellerIds, body.ConversationIds, body.IncludeAudio, body.Force);
            if (filters is null)
                return InvalidRange();

            return Results.Ok(await estimator.EstimateAsync(kind, filters, ct));
        });

        group.MapPost("/ai/analyses/run", (AiRunRequest body, AppDbContext db, AiJobEstimator estimator, CancellationToken ct) =>
            CreateJobAsync(AiJobKind.Analyze, body, db, estimator, ct));

        group.MapPost("/ai/syntheses/run", (AiRunRequest body, AppDbContext db, AiJobEstimator estimator, CancellationToken ct) =>
            CreateJobAsync(AiJobKind.Synthesize, body, db, estimator, ct));

        // O estado que manda na tela: rodada em andamento e a última de cada tipo.
        group.MapGet("/ai/status", async (AppDbContext db, CancellationToken ct) =>
        {
            var jobs = db.Set<AiJob>().AsNoTracking();

            var running = await jobs.FirstOrDefaultAsync(j => j.Active == true, ct);
            var last = await jobs
                .Where(j => j.Active == null)
                .GroupBy(j => j.Kind)
                .Select(g => g.OrderByDescending(j => j.CompletedAt).First())
                .ToListAsync(ct);

            AiJobDto? Latest(AiJobKind kind) =>
                last.FirstOrDefault(j => j.Kind == kind) is { } job ? AiJobDto.Of(job) : null;

            return Results.Ok(new AiStatusDto(
                running is null ? null : AiJobDto.Of(running),
                Latest(AiJobKind.Analyze),
                Latest(AiJobKind.Synthesize)));
        });

        group.MapGet("/ai/jobs/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var job = await db.Set<AiJob>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
            return job is null ? Results.NotFound() : Results.Ok(AiJobDto.Of(job));
        });

        return group;
    }

    private static async Task<IResult> CreateJobAsync(
        AiJobKind kind,
        AiRunRequest body,
        AppDbContext db,
        AiJobEstimator estimator,
        CancellationToken ct)
    {
        var filters = FiltersOf(
            body.From, body.To, body.SellerIds, body.ConversationIds, body.IncludeAudio, body.Force);
        if (filters is null)
            return InvalidRange();

        // Sem saldo, nem job é gravado: pedido que já se sabe que não vai rodar
        // só serviria para o usuário esperar por um erro.
        if (!(await estimator.EstimateAsync(kind, filters, ct)).Affordable)
            return Results.UnprocessableEntity(new { error = AiJobEstimator.NoBudgetMessage(kind) });

        var job = new AiJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            // Filtros congelados: o que roda é o que estava na tela ao confirmar.
            FiltersJson = JsonSerializer.Serialize(filters),
            Status = AiJobStatus.Pending,
            Active = true,
            CreatedAt = DateTime.UtcNow,
        };

        db.Add(job);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // O índice único da flag é quem decide quem entrou primeiro: dois
            // cliques simultâneos não furam a vaga.
            return Results.Conflict(new { error = "Já existe uma análise ou síntese em andamento." });
        }

        return Results.Accepted($"/api/v1/ai/jobs/{job.Id}", AiJobDto.Of(job));
    }

    private static AiJobFilters? FiltersOf(
        DateTime? from,
        DateTime? to,
        IReadOnlyList<Guid>? sellerIds,
        IReadOnlyList<Guid>? conversationIds,
        bool? includeAudio,
        bool? force)
    {
        var (fromUtc, toUtc) = Range(from, to);
        if (fromUtc >= toUtc)
            return null;

        return new AiJobFilters(
            fromUtc, toUtc, sellerIds ?? [], conversationIds ?? [], includeAudio ?? false, force ?? false);
    }

    private static AiJobKind KindOf(string? kind) =>
        string.Equals(kind, nameof(AiJobKind.Synthesize), StringComparison.OrdinalIgnoreCase)
            ? AiJobKind.Synthesize
            : AiJobKind.Analyze;

    private static IResult InvalidRange() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["from"] = ["'from' precisa ser anterior a 'to'."],
        });

    private static AiAnalysisFilter Filter(
        DateTime? from,
        DateTime? to,
        Guid? sellerId,
        string? status,
        string? lossReason,
        bool? divergent,
        bool? recontact)
    {
        var (fromUtc, toUtc) = Range(from, to);
        return new AiAnalysisFilter(fromUtc, toUtc, sellerId, status, lossReason, divergent, recontact);
    }

    private static string FileNameFor(AiAnalysisFilter filter, TimeZoneInfo timeZone) =>
        $"analises-ia-{Local(filter.From, timeZone):yyyy-MM-dd}-a-{Local(filter.To, timeZone):yyyy-MM-dd}.xlsx";

    private static DateTime Local(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);

    private static (DateTime From, DateTime To) Range(DateTime? from, DateTime? to)
    {
        var toUtc = UtcDates.ToUtc(to) ?? DateTime.UtcNow;
        return (UtcDates.ToUtc(from) ?? toUtc.AddDays(-30), toUtc);
    }
}
