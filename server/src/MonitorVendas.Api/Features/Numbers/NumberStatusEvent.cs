namespace MonitorVendas.Api.Features.Numbers;

// Histórico de conexão do número: alimenta uptime, contagem de bans e a
// exclusão de downtime nos denominadores das métricas.
public class NumberStatusEvent
{
    public long Id { get; set; }
    public Guid WhatsappNumberId { get; set; }
    public string State { get; set; } = string.Empty;
    public int? StatusReason { get; set; }
    public NumberStatus ResultingStatus { get; set; }
    public DateTime OccurredAt { get; set; }
}
