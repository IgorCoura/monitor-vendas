namespace MonitorVendas.Api.Features.Conversations;

// Desfecho da conversa, derivado da etiqueta mapeada ativa mais recente
// (ver OutcomeResolver). Uma conversa tem no máximo um desfecho: a última
// etiqueta aplicada é a que vale.
public class ConversationOutcome
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string OutcomeTypeCode { get; set; } = string.Empty;
    public string? LabelId { get; set; }
    public DateTime MarkedAt { get; set; }
}

// Registro das etiquetas da instância (LABELS_EDIT), usado para resolver
// labelId → nome quando a associação chega.
public class WhatsappLabel
{
    public Guid Id { get; set; }
    public string InstanceName { get; set; } = string.Empty;
    public string LabelId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
