using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;

namespace MonitorVendas.Api.Features.Contacts;

public record CreateContactShareRequest(Guid SenderNumberId, string Destination);

// Aviso de risco: o envio pode prosseguir, mas quem opera precisa ver isto antes.
public record ShareRiskWarning(string Code, string Message);

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
            bool? confirmRisk,
            CreateContactShareRequest request,
            AppDbContext db,
            ContactQueries queries,
            ReportQueries reports,
            Numbers.Health.NumberHealthQueries health,
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

            // As proteções anti-ban AVISAM, não impedem: o operador vê o risco e
            // decide. Sem o "sim" explícito o envio não é criado, para ninguém
            // disparar por engano num número que o WhatsApp já restringiu.
            var warnings = await RiskWarningsAsync(sender, options, reports, health, ct);
            if (warnings.Count > 0 && confirmRisk != true)
                return Results.Conflict(new
                {
                    error = "Enviar por este número agora tem risco de banimento. Confirme para enviar mesmo assim.",
                    requiresConfirmation = true,
                    warnings,
                });

            var share = new ContactShare
            {
                Id = Guid.NewGuid(),
                SenderNumberId = sender.Id,
                Destination = destination,
                TotalContacts = rows.Count,
                Status = ContactShareStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                RiskAcknowledged = confirmRisk == true,
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

    // Os riscos conhecidos de enviar por este número AGORA. Lista vazia = pode
    // enviar sem perguntar nada.
    private static async Task<List<ShareRiskWarning>> RiskWarningsAsync(
        WhatsappNumber sender,
        ContactShareOptions options,
        ReportQueries reports,
        Numbers.Health.NumberHealthQueries health,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var warnings = new List<ShareRiskWarning>();

        // O próprio WhatsApp já disse para parar (erro 463) — é o aviso mais forte.
        if (sender.SendingPausedUntil is { } paused && paused > now)
            warnings.Add(new("sendingPaused",
                $"O WhatsApp restringiu o envio por este número há pouco (erro 463). "
                + $"O recomendado é esperar até {paused:dd/MM HH:mm} UTC."));

        if (options.BusinessHoursOnly)
        {
            var calendar = await reports.BuildCalendarAsync(ct);
            if (calendar.BusinessTimeBetween(now, now.AddMinutes(1)) <= TimeSpan.Zero)
                warnings.Add(new("outsideBusinessHours",
                    "Agora está fora do horário comercial. Mensagem em massa de madrugada ou "
                    + "em feriado é padrão de robô, não de vendedor."));
        }

        var rows = await health.ListAsync(now.AddDays(-7), now, ct);
        if (rows.FirstOrDefault(r => r.NumberId == sender.Id) is { Level: "High" or "Critical" } risky)
            warnings.Add(new("health",
                $"Este número está com saúde {(risky.Level == "Critical" ? "crítica" : "em risco")} "
                + $"({risky.Score}/100) nos últimos 7 dias. Ver o detalhe em Cadastros."));

        return warnings;
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
