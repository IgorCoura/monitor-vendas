namespace MonitorVendas.Api.Features.Conversations;

public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid WhatsappNumberId { get; set; }
    public string WaMessageId { get; set; } = string.Empty;
    public MessageDirection Direction { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Text { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public enum MessageDirection
{
    Inbound = 0,
    Outbound = 1
}
