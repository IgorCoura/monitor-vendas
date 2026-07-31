namespace MonitorVendas.Api.Features.Ai;

public sealed class AiJobOptions
{
    public const string Section = "AiJob";

    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 5;

    // Teto de conversas por rodada — protege contra um filtro aberto de 90 dias
    // virar milhares de chamadas de IA sem querer.
    public int MaxConversationsPerRun { get; set; } = 2_000;
}
