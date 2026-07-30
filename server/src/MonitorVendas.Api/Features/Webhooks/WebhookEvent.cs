namespace MonitorVendas.Api.Features.Webhooks;

// Evento bruto recebido da Evolution (ou sintetizado pela reconciliação),
// processado de forma assíncrona pelo WebhookProcessor.
public class WebhookEvent
{
    public long Id { get; set; }
    public string InstanceName { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public string? DedupeKey { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
}
