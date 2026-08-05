using Microsoft.Extensions.Options;
using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Api.Features.Contacts;

public static class ContactsEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public static RouteGroupBuilder MapContactsEndpoints(this RouteGroupBuilder group)
    {
        // Prévia obrigatória antes de fundir cadastro, no mesmo idioma do
        // /proxies/allocation/preview: quem opera vê o que vai ser juntado antes
        // de juntar, porque fusão de contato não tem desfazer.
        group.MapGet("/contacts/lid-consolidation", async (
            LidConsolidationService service, CancellationToken ct) =>
            Results.Ok(await service.PlanAsync(ct)));

        group.MapPost("/contacts/lid-consolidation", async (
            LidConsolidationService service, CancellationToken ct) =>
            Results.Ok(await service.ApplyAsync(ct)));

        // Prévia da exportação: os mesmos filtros, paginada para caber na tela.
        group.MapGet("/contacts", async (
            DateTime? from,
            DateTime? to,
            Guid? sellerId,
            string? outcomeTypes,
            bool? banned,
            int? page,
            int? pageSize,
            ContactQueries queries,
            CancellationToken ct) =>
        {
            var filter = ContactFilter.TryCreate(from, to, sellerId, outcomeTypes, banned);
            if (filter is null)
                return InvalidRange();

            var rows = await queries.ListAsync(filter, ct);

            var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
            var current = Math.Max(page ?? 1, 1);
            var items = rows.Skip((current - 1) * size).Take(size).ToList();

            return Results.Ok(new ContactPageDto(items, current, size, rows.Count));
        });

        // Mesma consulta, sem paginação, embrulhada em planilha.
        group.MapGet("/contacts/export", async (
            DateTime? from,
            DateTime? to,
            Guid? sellerId,
            string? outcomeTypes,
            bool? banned,
            HttpResponse response,
            ContactQueries queries,
            IOptions<MetricsOptions> metrics,
            CancellationToken ct) =>
        {
            var filter = ContactFilter.TryCreate(from, to, sellerId, outcomeTypes, banned);
            if (filter is null)
                return InvalidRange();

            var rows = await queries.ListAsync(filter, ct);
            if (rows.Count > ContactQueries.MaxRows)
            {
                response.Headers["X-Truncated"] = "true";
                rows = [.. rows.Take(ContactQueries.MaxRows)];
            }

            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(metrics.Value.TimeZone);
            var file = ContactWorkbookWriter.Build(rows, timeZone);

            return Results.File(file, ContactWorkbookWriter.ContentType, FileNameFor(filter));
        });

        return group;
    }

    private static string FileNameFor(ContactFilter filter)
    {
        var range = (filter.FromUtc, filter.ToUtc) switch
        {
            ({ } from, { } to) => $"{from:yyyy-MM-dd}-a-{to:yyyy-MM-dd}",
            ({ } from, null) => $"desde-{from:yyyy-MM-dd}",
            (null, { } to) => $"ate-{to:yyyy-MM-dd}",
            _ => "completo",
        };

        return $"contatos-{range}.xlsx";
    }

    internal static IResult InvalidRange() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["from"] = ["'from' precisa ser anterior a 'to'."],
        });
}
