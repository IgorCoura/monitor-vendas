namespace MonitorVendas.Api.Features.Metrics;

public sealed class MetricsOptions
{
    public const string Section = "Metrics";

    public string TimeZone { get; set; } = "America/Sao_Paulo";
    public int BusinessDayStartHour { get; set; } = 9;
    public int BusinessDayEndHour { get; set; } = 18;
    public bool SaturdayEnabled { get; set; } = true;
    public int SaturdayStartHour { get; set; } = 9;
    public int SaturdayEndHour { get; set; } = 13;
    public string SaleLabelName { get; set; } = "venda";
    public int NewConversationWindowDays { get; set; } = 15;
    public int AnswerWindowBusinessHours { get; set; } = 24;
    public int FollowUpGapBusinessHours { get; set; } = 24;

    // Quantos dias ANTES do período as mensagens são carregadas, só para achar de
    // qual mensagem do cliente vem a espera que o vendedor fecha logo no começo da
    // janela (cliente escreve 23h, vendedor responde 9h). Nada disso é contado.
    public int ResponseLookbackDays { get; set; } = 2;

    // 0 desliga o cache de relatórios.
    public int CacheSeconds { get; set; } = 60;

    // Agregado diário: dias fechados vêm da tabela em vez de reprocessar mensagens.
    public bool AggregationEnabled { get; set; } = true;
    public int AggregationIntervalSeconds { get; set; } = 120;
    public bool UseDailyAggregates { get; set; } = true;

    // Períodos até este tamanho são calculados ao vivo (mediana exata).
    public int LiveCalculationMaxDays { get; set; } = 7;
}
