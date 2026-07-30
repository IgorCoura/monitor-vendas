namespace MonitorVendas.Api.Features.ReportExport;

public enum ReportExportStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

// O pedido de exportação, com os filtros congelados no momento em que o usuário
// confirmou. O arquivo mora aqui e é apagado por retenção — planilha é
// descartável, não merece volume no compose.
public class ReportExport
{
    public Guid Id { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string FiltersJson { get; set; } = string.Empty;
    public ReportExportStatus Status { get; set; }

    public int TotalConversations { get; set; }
    public int AnalyzedConversations { get; set; }
    public int CachedConversations { get; set; }
    public int SkippedConversations { get; set; }
    public decimal CostBrl { get; set; }

    // O que o job está fazendo agora, para a tela não parecer travada durante a
    // espera por cota do provedor de IA.
    public string? Phase { get; set; }

    public string? Error { get; set; }
    public byte[]? File { get; set; }
    public string? FileName { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
