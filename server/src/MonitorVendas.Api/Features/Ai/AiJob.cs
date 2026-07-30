namespace MonitorVendas.Api.Features.Ai;

public enum AiJobKind
{
    // Relê as conversas do filtro ignorando o cache.
    Analyze,
    // Refaz a síntese dos vendedores do filtro a partir das leituras correntes.
    Synthesize
}

public enum AiJobStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

// Pedido feito pela tela de análises. Roda em background pelo mesmo motivo da
// exportação: com a cota do provedor, uma rodada leva minutos.
public class AiJob
{
    public Guid Id { get; set; }
    public AiJobKind Kind { get; set; }
    public string FiltersJson { get; set; } = string.Empty;
    public AiJobStatus Status { get; set; }

    public int Total { get; set; }
    public int Processed { get; set; }
    public int Skipped { get; set; }
    public decimal CostBrl { get; set; }

    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
