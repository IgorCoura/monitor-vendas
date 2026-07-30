namespace MonitorVendas.Api.Features.Ai;

public enum AiUsageStatus
{
    // Saldo comprometido pela estimativa, antes de chamar o provedor.
    Reserved,
    // Custo real conhecido (com margem aplicada).
    Settled,
    // A chamada não chegou a gerar nada: o dinheiro volta para a janela.
    Released
}

// Um registro por chamada ao provedor. O saldo não é um campo guardado em lugar
// nenhum — é a soma destes registros dentro da janela corrente.
public class AiUsage
{
    public Guid Id { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public AiUsageStatus Status { get; set; }
    public decimal EstimatedBrl { get; set; }
    public decimal? ActualBrl { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public DateTime WindowStart { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SettledAt { get; set; }

    // O que a janela já consumiu: enquanto não há custo real, vale a estimativa.
    public decimal CommittedBrl => Status switch
    {
        AiUsageStatus.Released => 0m,
        AiUsageStatus.Settled => ActualBrl ?? 0m,
        _ => EstimatedBrl,
    };
}
