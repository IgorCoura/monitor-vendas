using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;

namespace MonitorVendas.Api.Features.Contacts;

public record CreateContactShareRequest(Guid SenderNumberId, string Destination);

public record ContactShareDto(
    Guid Id,
    Guid SenderNumberId,
    string SenderPhone,
    string Destination,
    int TotalContacts,
    int TotalMessages,
    int SentMessages,
    string Status,
    string? Error,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public static class ContactShareEndpoints
{
    public static RouteGroupBuilder MapContactShareEndpoints(this RouteGroupBuilder group)
    {
        // Enfileira o envio da lista filtrada por WhatsApp. As mensagens são
        // montadas e gravadas agora; quem manda é o serviço em background.
        group.MapPost("/contacts/share", async (
            DateTime? from,
            DateTime? to,
            Guid? sellerId,
            string? outcomeTypes,
            bool? banned,
            CreateContactShareRequest request,
            AppDbContext db,
            ContactQueries queries,
            IOptions<ContactShareOptions> shareOptions,
            IOptions<MetricsOptions> metrics,
            CancellationToken ct) =>
        {
            var filter = ContactFilter.TryCreate(from, to, sellerId, outcomeTypes, banned);
            if (filter is null)
                return ContactsEndpoints.InvalidRange();

            var destination = new string([.. (request.Destination ?? "").Where(char.IsDigit)]);
            if (destination.Length < 10)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["destination"] = ["Informe o número com DDI e DDD, apenas dígitos (ex.: 5511999999999)."],
                });

            var sender = await db.Set<WhatsappNumber>().AsNoTracking()
                .FirstOrDefaultAsync(n => n.Id == request.SenderNumberId, ct);
            if (sender is null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["senderNumberId"] = ["Número remetente não encontrado."],
                });

            if (sender.Status != NumberStatus.Active)
                return Results.Conflict(new { error = $"O número {sender.Phone} não está conectado — escolha outro remetente." });

            var rows = await queries.ListAsync(filter, ct);
            if (rows.Count == 0)
                return Results.Conflict(new { error = "Nenhum contato com os filtros atuais." });

            var options = shareOptions.Value;
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(metrics.Value.TimeZone);
            var bodies = ContactMessageBuilder.Build(rows, filter.FromUtc, filter.ToUtc, timeZone, options.MaxCharsPerMessage);

            if (bodies.Count > options.MaxMessagesPerShare)
                return Results.Conflict(new
                {
                    error = $"A lista daria {bodies.Count} mensagens (máximo {options.MaxMessagesPerShare}). "
                          + "Aperte o filtro para não sobrecarregar o número.",
                });

            var share = new ContactShare
            {
                Id = Guid.NewGuid(),
                SenderNumberId = sender.Id,
                Destination = destination,
                TotalContacts = rows.Count,
                Status = ContactShareStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            };

            db.Add(share);
            db.AddRange(bodies.Select((body, index) => new ContactShareMessage
            {
                Id = Guid.NewGuid(),
                ContactShareId = share.Id,
                Sequence = index + 1,
                Body = body,
            }));
            await db.SaveChangesAsync(ct);

            return Results.Accepted(
                $"/api/v1/contacts/share/{share.Id}",
                new ContactShareDto(share.Id, sender.Id, sender.Phone, share.Destination, share.TotalContacts,
                    bodies.Count, 0, share.Status.ToString(), null, share.CreatedAt, null));
        });

        group.MapGet("/contacts/share/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var dto = await LoadAsync(db, id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        return group;
    }

    private static async Task<ContactShareDto?> LoadAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var share = await db.Set<ContactShare>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (share is null)
            return null;

        var sender = await db.Set<WhatsappNumber>().AsNoTracking()
            .Where(n => n.Id == share.SenderNumberId)
            .Select(n => n.Phone)
            .FirstOrDefaultAsync(ct);

        var counts = await db.Set<ContactShareMessage>().AsNoTracking()
            .Where(m => m.ContactShareId == id)
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Sent = g.Count(m => m.SentAt != null) })
            .FirstOrDefaultAsync(ct);

        return new ContactShareDto(
            share.Id, share.SenderNumberId, sender ?? string.Empty, share.Destination, share.TotalContacts,
            counts?.Total ?? 0, counts?.Sent ?? 0, share.Status.ToString(), share.Error, share.CreatedAt, share.CompletedAt);
    }
}
