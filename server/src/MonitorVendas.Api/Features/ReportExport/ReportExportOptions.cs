namespace MonitorVendas.Api.Features.ReportExport;

public sealed class ReportExportOptions
{
    public const string Section = "ReportExport";

    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 5;

    // Planilha pronta é descartável: depois disso o arquivo some do banco.
    public int RetentionHours { get; set; } = 48;

    // Teto de conversas analisadas por exportação — protege contra um filtro
    // aberto de 90 dias virar milhares de chamadas de IA sem querer.
    public int MaxConversationsPerExport { get; set; } = 2_000;

    // Prazo da fase de IA inteira. Estourado, o que faltou sai marcado e a
    // planilha é entregue: o relatório já está pronto e ninguém deve esperar
    // minutos por causa de limite de cota do provedor.
    public int AiDeadlineSeconds { get; set; } = 120;

    // Exportações rodam em paralelo porque uma planilha sem IA leva ~0,2s e não
    // pode ficar minutos na fila atrás de uma que espera cota do provedor.
    public int MaxConcurrentExports { get; set; } = 3;
}
