using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Common;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Features.ReportExport;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Api.Features.Ai;

public sealed record AiAnalysisRowDto(
    Guid ConversationId,
    Guid AnalysisId,
    Guid? SellerId,
    string SellerName,
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
    int Versions);

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
    IReadOnlyList<Guid>? ConversationIds);

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
            AppDbContext db,
            CancellationToken ct) =>
        {
            var (fromUtc, toUtc) = Range(from, to);
            var take = Math.Clamp(pageSize ?? 50, 1, 200);
            var skip = (Math.Max(1, page ?? 1) - 1) * take;

            var typeNames = await db.Set<ConversationOutcomeType>().AsNoTracking()
                .ToDictionaryAsync(t => t.Code, t => t.Name, ct);

            var query =
                from analysis in db.Set<ConversationAiAnalysis>().AsNoTracking().Where(a => a.IsCurrent)
                join conversation in db.Set<Conversation>().AsNoTracking() on analysis.ConversationId equals conversation.Id
                join number in db.Set<WhatsappNumber>().AsNoTracking() on conversation.WhatsappNumberId equals number.Id
                join seller in db.Set<Seller>().AsNoTracking() on number.SellerId equals seller.Id
                join contact in db.Set<Contact>().AsNoTracking() on conversation.ContactId equals contact.Id
                join outcome in db.Set<ConversationOutcome>().AsNoTracking() on conversation.Id equals outcome.ConversationId into outcomes
                from outcome in outcomes.DefaultIfEmpty()
                where conversation.LastMessageAt >= fromUtc && conversation.StartedAt <= toUtc
                select new
                {
                    Analysis = analysis,
                    Conversation = conversation,
                    Seller = seller,
                    contact.PushName,
                    contact.RemoteJid,
                    RealCode = outcome != null ? outcome.OutcomeTypeCode : null,
                };

            if (sellerId is not null)
                query = query.Where(x => x.Seller.Id == sellerId);

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(x => x.Analysis.StatusCode == status);

            if (!string.IsNullOrWhiteSpace(lossReason))
                query = query.Where(x => x.Analysis.LossReason == lossReason);

            if (recontact is not null)
                query = query.Where(x => x.Analysis.ShouldRecontact == recontact);

            // Divergência é comparação com a etiqueta; "em andamento" conta como
            // ausência de desfecho, igual ao AiRowMapper.
            if (divergent is not null)
            {
                query = divergent.Value
                    ? query.Where(x => x.Analysis.StatusCode == ConversationAiAnalysis.Open
                        ? x.RealCode != null
                        : x.Analysis.StatusCode != x.RealCode)
                    : query.Where(x => x.Analysis.StatusCode == ConversationAiAnalysis.Open
                        ? x.RealCode == null
                        : x.Analysis.StatusCode == x.RealCode);
            }

            var total = await query.CountAsync(ct);
            var rows = await query
                .OrderByDescending(x => x.Conversation.LastMessageAt)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);

            var conversationIds = rows.Select(r => r.Conversation.Id).ToList();
            var versions = await db.Set<ConversationAiAnalysis>().AsNoTracking()
                .Where(a => conversationIds.Contains(a.ConversationId))
                .GroupBy(a => a.ConversationId)
                .Select(g => new { ConversationId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ConversationId, x => x.Count, ct);

            var items = rows.Select(r =>
            {
                var aiCode = r.Analysis.StatusCode == ConversationAiAnalysis.Open ? null : r.Analysis.StatusCode;
                var phone = ConversationAiWorkset.PhoneOf(r.RemoteJid) ?? "—";

                return new AiAnalysisRowDto(
                    r.Conversation.Id,
                    r.Analysis.Id,
                    r.Seller.Id,
                    r.Seller.Name,
                    r.PushName ?? phone,
                    phone,
                    r.Conversation.StartedAt,
                    r.Conversation.LastMessageAt,
                    r.RealCode is null ? null : typeNames.GetValueOrDefault(r.RealCode, r.RealCode),
                    aiCode is null ? "Em andamento" : typeNames.GetValueOrDefault(aiCode, aiCode),
                    r.Analysis.StatusCode,
                    r.Analysis.StatusConfidence,
                    !string.Equals(aiCode, r.RealCode, StringComparison.OrdinalIgnoreCase),
                    r.Analysis.StatusEvidence,
                    AiSheetsWriter.FriendlyLossReason(r.Analysis.LossReason),
                    r.Analysis.AskedForSale,
                    r.Analysis.IgnoredBuyingSignal,
                    r.Analysis.Objections,
                    r.Analysis.ShouldRecontact,
                    r.Analysis.RecontactReason,
                    r.Analysis.SuggestedMessage,
                    r.Analysis.Interest,
                    r.Analysis.Summary,
                    r.Analysis.ConductAlert,
                    r.Analysis.Model,
                    r.Analysis.AnalyzedAt,
                    versions.GetValueOrDefault(r.Conversation.Id, 1));
            }).ToList();

            return Results.Ok(new AiAnalysisPageDto(items, Math.Max(1, page ?? 1), take, total));
        });

        // A síntese mais recente de cada vendedor, marcada como desatualizada
        // quando as leituras que a geraram já não são as correntes.
        group.MapGet("/ai/syntheses", async (Guid? sellerId, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.Set<SellerAiSynthesis>().AsNoTracking().AsQueryable();
            if (sellerId is not null)
                query = query.Where(s => s.SellerId == sellerId);

            var latest = await query
                .GroupBy(s => s.SellerId)
                .Select(g => g.OrderByDescending(s => s.CreatedAt).First())
                .ToListAsync(ct);

            var currentBySeller = await db.Set<ConversationAiAnalysis>().AsNoTracking()
                .Where(a => a.IsCurrent)
                .Join(db.Set<Conversation>().AsNoTracking(), a => a.ConversationId, c => c.Id, (a, c) => new { a.Id, c.WhatsappNumberId })
                .Join(db.Set<WhatsappNumber>().AsNoTracking(), x => x.WhatsappNumberId, n => n.Id, (x, n) => new { x.Id, n.SellerId })
                .ToListAsync(ct);

            var hashes = currentBySeller
                .GroupBy(x => x.SellerId)
                .ToDictionary(g => g.Key, g => SellerAiSynthesis.HashOf(g.Select(x => x.Id)));

            return Results.Ok(latest.Select(s => new AiSynthesisDto(
                s.SellerId,
                s.SellerName,
                s.Overview,
                SellerAiSynthesis.Split(s.Strengths),
                SellerAiSynthesis.Split(s.Improvements),
                s.DominantLossPattern,
                s.TrainingSuggestion,
                s.ConversationsCount,
                s.Model,
                s.CreatedAt,
                hashes.GetValueOrDefault(s.SellerId) != s.InputsHash)));
        });

        // A taxonomia de perda é fechada e definida no schema do prompt: a tela lê
        // daqui em vez de repetir a lista e sair de sincronia.
        group.MapGet("/ai/loss-reasons", () => Results.Ok(
            AiAnalysisSchema.LossReasons.Select(code => new
            {
                Code = code,
                Label = AiSheetsWriter.FriendlyLossReason(code),
            })));

        group.MapPost("/ai/analyses/run", (AiRunRequest body, AppDbContext db, CancellationToken ct) =>
            CreateJobAsync(AiJobKind.Analyze, body, db, ct));

        group.MapPost("/ai/syntheses/run", (AiRunRequest body, AppDbContext db, CancellationToken ct) =>
            CreateJobAsync(AiJobKind.Synthesize, body, db, ct));

        group.MapGet("/ai/jobs/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var job = await db.Set<AiJob>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
            return job is null ? Results.NotFound() : Results.Ok(AiJobDto.Of(job));
        });

        return group;
    }

    private static async Task<IResult> CreateJobAsync(AiJobKind kind, AiRunRequest body, AppDbContext db, CancellationToken ct)
    {
        var (fromUtc, toUtc) = Range(body.From, body.To);
        if (fromUtc >= toUtc)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = ["'from' precisa ser anterior a 'to'."],
            });

        var job = new AiJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            // Filtros congelados: o que roda é o que estava na tela ao confirmar.
            FiltersJson = JsonSerializer.Serialize(new AiJobFilters(
                fromUtc, toUtc, body.SellerIds ?? [], body.ConversationIds ?? [])),
            Status = AiJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        db.Add(job);
        await db.SaveChangesAsync(ct);

        return Results.Accepted($"/api/v1/ai/jobs/{job.Id}", AiJobDto.Of(job));
    }

    private static (DateTime From, DateTime To) Range(DateTime? from, DateTime? to)
    {
        var toUtc = UtcDates.ToUtc(to) ?? DateTime.UtcNow;
        return (UtcDates.ToUtc(from) ?? toUtc.AddDays(-30), toUtc);
    }
}
