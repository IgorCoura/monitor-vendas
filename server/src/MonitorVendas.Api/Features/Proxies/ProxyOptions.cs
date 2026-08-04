namespace MonitorVendas.Api.Features.Proxies;

public sealed class ProxyOptions
{
    public const string Section = "Proxy";

    // Master switch de infraestrutura (desliga os background services nos
    // testes). O liga/desliga OPERACIONAL é o interruptor da tela, persistido
    // no banco, para valer sem redeploy.
    public bool Enabled { get; set; } = true;

    // Fallback da capacidade: vale quando o fornecedor não informa o limite de
    // dispositivos e não há ajuste manual. 2 é a decisão de agora — a
    // vizinhança de um IPv4 dedicado é toda nossa, então o que pesa é quem
    // divide, não quantos, e 2 mantém o raio de dano pequeno.
    public int DefaultCapacity { get; set; } = 2;

    // socks5 é o que o Baileys quer; http também funciona.
    public string Protocol { get; set; } = "socks5";

    public int SuspectBansPerWindow { get; set; } = 2;
    public int SuspectWindowDays { get; set; } = 30;

    public int ApplierIntervalSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 5;
}
