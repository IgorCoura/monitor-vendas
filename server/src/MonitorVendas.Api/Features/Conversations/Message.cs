namespace MonitorVendas.Api.Features.Conversations;

public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid WhatsappNumberId { get; set; }

    // Vendedor dono do número no momento da mensagem — ver Conversation.SellerId.
    public Guid SellerId { get; set; }
    public string WaMessageId { get; set; } = string.Empty;
    public MessageDirection Direction { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Text { get; set; }

    // Só para áudio e vídeo: entra na transcrição e, quando o áudio vai junto para
    // a IA, é o que permite estimar o custo antes de enviar.
    public int? DurationSeconds { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public enum MessageDirection
{
    Inbound = 0,
    Outbound = 1
}
