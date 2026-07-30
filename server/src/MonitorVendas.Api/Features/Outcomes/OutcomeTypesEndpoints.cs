using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;

namespace MonitorVendas.Api.Features.Outcomes;

public record OutcomeTermDto(Guid Id, string Term);

public record OutcomeTypeDto(string Code, string Name, int SortOrder, bool Active, IReadOnlyList<OutcomeTermDto> Terms);

public record CreateOutcomeTypeRequest(string Code, string Name);

public record UpdateOutcomeTypeRequest(string Name, bool Active, int SortOrder);

public record CreateTermRequest(string Term);

public record LabelSuggestionDto(string LabelId, string Name, int Conversations, string? MappedToTypeCode);

public static class OutcomeTypesEndpoints
{
    public static RouteGroupBuilder MapOutcomeTypesEndpoints(this RouteGroupBuilder group)
    {
        var types = group.MapGroup("/outcome-types");

        types.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var all = await db.Set<ConversationOutcomeType>().AsNoTracking()
                .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
                .ToListAsync(ct);

            var terms = await db.Set<OutcomeLabelTerm>().AsNoTracking()
                .OrderBy(t => t.Term)
                .ToListAsync(ct);

            var byType = terms.ToLookup(t => t.OutcomeTypeCode);

            return Results.Ok(all.Select(t => new OutcomeTypeDto(
                t.Code, t.Name, t.SortOrder, t.Active,
                [.. byType[t.Code].Select(term => new OutcomeTermDto(term.Id, term.Term))])));
        });

        types.MapPost("/", async (
            CreateOutcomeTypeRequest request,
            AppDbContext db,
            OutcomeCatalogVersion version,
            CancellationToken ct) =>
        {
            var code = LabelNormalizer.Normalize(request.Code).Replace(' ', '-');
            if (code.Length == 0 || string.IsNullOrWhiteSpace(request.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["code"] = ["Informe um código e um nome para o tipo."],
                });

            if (await db.Set<ConversationOutcomeType>().AnyAsync(t => t.Code == code, ct))
                return Results.Conflict(new { error = "Já existe um tipo com esse código." });

            var maxOrder = await db.Set<ConversationOutcomeType>().MaxAsync(t => (int?)t.SortOrder, ct) ?? 0;
            var type = new ConversationOutcomeType
            {
                Code = code,
                Name = request.Name.Trim(),
                SortOrder = maxOrder + 1,
                Active = true,
            };

            db.Add(type);
            await db.SaveChangesAsync(ct);
            version.Bump();

            return Results.Created($"/api/v1/outcome-types/{code}", new OutcomeTypeDto(type.Code, type.Name, type.SortOrder, type.Active, []));
        });

        types.MapPut("/{code}", async (
            string code,
            UpdateOutcomeTypeRequest request,
            AppDbContext db,
            OutcomeCatalogVersion version,
            OutcomeReconciler reconciler,
            CancellationToken ct) =>
        {
            var type = await db.Set<ConversationOutcomeType>().FirstOrDefaultAsync(t => t.Code == code, ct);
            if (type is null)
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["O nome do tipo é obrigatório."],
                });

            type.Name = request.Name.Trim();
            type.Active = request.Active;
            type.SortOrder = request.SortOrder;
            await db.SaveChangesAsync(ct);
            version.Bump();
            // Desativar um tipo tira os desfechos dele de circulação.
            await reconciler.ReconcileAllAsync(ct);

            return Results.Ok(new OutcomeTypeDto(type.Code, type.Name, type.SortOrder, type.Active, []));
        });

        types.MapDelete("/{code}", async (
            string code,
            AppDbContext db,
            OutcomeCatalogVersion version,
            OutcomeReconciler reconciler,
            CancellationToken ct) =>
        {
            if (code is OutcomeTypeCodes.Sale)
                return Results.Conflict(new { error = "O tipo de venda não pode ser removido." });

            var type = await db.Set<ConversationOutcomeType>().FirstOrDefaultAsync(t => t.Code == code, ct);
            if (type is null)
                return Results.NotFound();

            await db.Set<OutcomeLabelTerm>().Where(t => t.OutcomeTypeCode == code).ExecuteDeleteAsync(ct);
            db.Remove(type);
            await db.SaveChangesAsync(ct);
            version.Bump();
            await reconciler.ReconcileAllAsync(ct);

            return Results.NoContent();
        });

        types.MapPost("/{code}/terms", async (
            string code,
            CreateTermRequest request,
            AppDbContext db,
            OutcomeCatalogVersion version,
            OutcomeReconciler reconciler,
            CancellationToken ct) =>
        {
            var type = await db.Set<ConversationOutcomeType>().FirstOrDefaultAsync(t => t.Code == code, ct);
            if (type is null)
                return Results.NotFound();

            var key = LabelNormalizer.Normalize(request.Term);
            if (key.Length == 0)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["term"] = ["Informe a etiqueta."],
                });

            var existing = await db.Set<OutcomeLabelTerm>().FirstOrDefaultAsync(t => t.NormalizedKey == key, ct);
            if (existing is not null)
                return Results.Conflict(new
                {
                    error = existing.OutcomeTypeCode == code
                        ? "Esta etiqueta já está nesse tipo."
                        : $"Esta etiqueta já está no tipo '{existing.OutcomeTypeCode}'.",
                });

            var term = new OutcomeLabelTerm
            {
                Id = Guid.NewGuid(),
                OutcomeTypeCode = code,
                Term = request.Term.Trim(),
                NormalizedKey = key,
                CreatedAt = DateTime.UtcNow,
            };

            db.Add(term);
            await db.SaveChangesAsync(ct);
            version.Bump();
            // Conversas já etiquetadas com este termo passam a contar agora.
            await reconciler.ReconcileAllAsync(ct);

            return Results.Created($"/api/v1/outcome-types/{code}/terms/{term.Id}", new OutcomeTermDto(term.Id, term.Term));
        });

        types.MapDelete("/{code}/terms/{termId:guid}", async (
            string code,
            Guid termId,
            AppDbContext db,
            OutcomeCatalogVersion version,
            OutcomeReconciler reconciler,
            CancellationToken ct) =>
        {
            var term = await db.Set<OutcomeLabelTerm>()
                .FirstOrDefaultAsync(t => t.Id == termId && t.OutcomeTypeCode == code, ct);
            if (term is null)
                return Results.NotFound();

            db.Remove(term);
            await db.SaveChangesAsync(ct);
            version.Bump();
            await reconciler.ReconcileAllAsync(ct);

            return Results.NoContent();
        });

        // Etiquetas que existem de verdade nos WhatsApps conectados, com quantas
        // conversas cada uma marca — evita o usuário adivinhar o texto exato.
        group.MapGet("/outcome-labels/suggestions", async (
            AppDbContext db,
            OutcomeLabelMatcher matcher,
            CancellationToken ct) =>
        {
            var labels = await db.Set<WhatsappLabel>().AsNoTracking().ToListAsync(ct);
            var usage = await db.Set<ConversationLabel>().AsNoTracking()
                .Where(l => l.RemovedAt == null)
                .GroupBy(l => l.LabelId)
                .Select(g => new { LabelId = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var usageByLabel = usage.ToDictionary(u => u.LabelId, u => u.Count);
            var map = await matcher.GetMapAsync(db, ct);

            var suggestions = labels
                .GroupBy(l => l.Name)
                .Select(g =>
                {
                    var labelIds = g.Select(l => l.LabelId).Distinct().ToList();
                    return new LabelSuggestionDto(
                        labelIds[0],
                        g.Key,
                        labelIds.Sum(id => usageByLabel.GetValueOrDefault(id)),
                        map.GetValueOrDefault(LabelNormalizer.Normalize(g.Key)));
                })
                .OrderByDescending(s => s.Conversations)
                .ThenBy(s => s.Name)
                .ToList();

            return Results.Ok(suggestions);
        });

        return group;
    }
}
