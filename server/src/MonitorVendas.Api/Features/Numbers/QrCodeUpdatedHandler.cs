using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Webhooks;

namespace MonitorVendas.Api.Features.Numbers;

// A Evolution regenera o QR a cada ~30 s e avisa por aqui. Sem guardar o novo, a
// tela continuaria mostrando o primeiro — que o WhatsApp já recusa, sem dizer
// por quê.
public sealed class QrCodeUpdatedHandler : IWebhookEventHandler
{
    public string EventType => "QRCODE_UPDATED";

    public async Task HandleAsync(WebhookEvent evt, AppDbContext db, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(evt.Payload);
        if (WebhookPayload.GetData(evt.Payload, doc) is not { } data)
            return;

        // O payload aninha em `qrcode` em alguns builds e vem plano em outros.
        if (data.TryGetProperty("qrcode", out var nested) && nested.ValueKind == JsonValueKind.Object)
            data = nested;

        var code = WebhookPayload.GetString(data, "code");
        var base64 = WebhookPayload.GetString(data, "base64");
        if (code is null && base64 is null)
            return;

        var session = await db.Set<PairingSession>()
            .FirstOrDefaultAsync(s => s.InstanceName == evt.InstanceName && s.Active == true, ct);

        // QR de instância que não está pareando é de uma tentativa já encerrada.
        if (session is null || session.Status != PairingStatus.AwaitingScan)
            return;

        session.QrCode = code ?? session.QrCode;
        session.QrBase64 = base64 ?? session.QrBase64;
    }
}
