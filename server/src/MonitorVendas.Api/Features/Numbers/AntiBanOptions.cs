namespace MonitorVendas.Api.Features.Numbers;

public sealed class AntiBanOptions
{
    public const string Section = "AntiBan";

    // Quanto tempo o número fica sem enviar depois de o WhatsApp avisar que a
    // conta chegou ao limite de contato frio (erro 463). Insistir é empurrar o
    // número para o ban.
    public int SendPauseHours { get; set; } = 12;

    // Reconexão bloqueada depois de um ban (statusReason 403): a escalada
    // documentada é 24h → 48h → vitalício, dirigida por reconexão insistente
    // durante a punição.
    public int BanCooldownHours { get; set; } = 24;

    // Cotas por número. A Evolution não tem rate limit nem fila embutidos
    // (issue #2538, fechada como "not planned"), então ou o controle é nosso ou
    // não existe. Convivem com a curva de aquecimento: vale sempre o MENOR dos
    // dois tetos.
    public int MaxMessagesPerHour { get; set; } = 30;
    public int MaxMessagesPerDay { get; set; } = 300;
}
