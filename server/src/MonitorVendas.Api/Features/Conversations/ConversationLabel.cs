namespace MonitorVendas.Api.Features.Conversations;

// TODA associação etiqueta↔conversa é registrada, mesmo quando a etiqueta ainda
// não representa nenhum tipo de desfecho. É o que permite reavaliar o passado
// quando o usuário aceita uma etiqueta nova.
public class ConversationLabel
{
    public Guid ConversationId { get; set; }
    public string LabelId { get; set; } = string.Empty;
    public string? LabelName { get; set; }
    public DateTime AppliedAt { get; set; }
    public DateTime? RemovedAt { get; set; }

    public bool IsActive => RemovedAt is null;
}
