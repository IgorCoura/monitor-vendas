namespace MonitorVendas.Api.Features.Contacts;

// Envio da lista de contatos por WhatsApp. O corpo das mensagens é congelado no
// momento do pedido (ContactShareMessage): o serviço em background não
// reconsulta o banco, senão o que chega seria diferente do que foi confirmado.
public class ContactShare
{
    public Guid Id { get; set; }
    public Guid SenderNumberId { get; set; }
    public string Destination { get; set; } = string.Empty;
    public int TotalContacts { get; set; }
    public ContactShareStatus Status { get; set; } = ContactShareStatus.Pending;
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum ContactShareStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2
}

public class ContactShareMessage
{
    public Guid Id { get; set; }
    public Guid ContactShareId { get; set; }
    public int Sequence { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime? SentAt { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }

    // Id devolvido pela Evolution: o webhook da própria mensagem volta como
    // MESSAGES_UPSERT e seria contado como mensagem enviada do vendedor.
    public string? WaMessageId { get; set; }
}
