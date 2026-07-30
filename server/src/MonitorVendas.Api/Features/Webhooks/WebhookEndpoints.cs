using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Api.Features.Webhooks;

public static class WebhookEndpoints
{
    public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/webhooks/evolution/{secret}", async (
            string secret,
            HttpRequest request,
            AppDbContext db,
            IOptions<WebhookOptions> options,
            CancellationToken ct) =>
        {
            // Secret errado responde 404 para não revelar que a rota existe.
            if (string.IsNullOrEmpty(options.Value.Secret) || !string.Equals(secret, options.Value.Secret, StringComparison.Ordinal))
                return Results.NotFound();

            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);

            string? eventType = null;
            string? instance = null;
            string? dedupeKey = null;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                eventType = root.TryGetProperty("event", out var e) ? e.GetString() : null;
                instance = root.TryGetProperty("instance", out var i) ? i.GetString() : null;

                if (eventType is not null && instance is not null && root.TryGetProperty("data", out var data))
                    dedupeKey = BuildDedupeKey(Normalize(eventType), instance, data);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(instance))
                return Results.BadRequest();

            if (dedupeKey is not null && await db.Set<WebhookEvent>().AnyAsync(w => w.DedupeKey == dedupeKey, ct))
                return Results.Ok();

            db.Add(new WebhookEvent
            {
                InstanceName = instance,
                EventType = Normalize(eventType),
                Payload = body,
                DedupeKey = dedupeKey,
                ReceivedAt = DateTime.UtcNow
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Corrida no índice único de dedupe: outro request gravou o mesmo evento primeiro.
            }

            return Results.Ok();
        });

        return group;
    }

    public static string Normalize(string eventType) =>
        eventType.Trim().Replace('.', '_').ToUpperInvariant();

    public static string? BuildDedupeKey(string normalizedEventType, string instance, JsonElement data)
    {
        if (normalizedEventType is not ("MESSAGES_UPSERT" or "SEND_MESSAGE"))
            return null;

        if (data.TryGetProperty("key", out var key) &&
            key.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return $"{instance}:MESSAGES_UPSERT:{id.GetString()}";
        }

        return null;
    }
}
