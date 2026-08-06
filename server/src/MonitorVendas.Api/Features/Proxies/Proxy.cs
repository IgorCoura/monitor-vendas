namespace MonitorVendas.Api.Features.Proxies;

// Um proxy contratado no fornecedor. O catálogo dele é espelho, não fonte de
// verdade: quem decide qual número sai por onde é a nossa tabela de atribuições.
// Proxy que some da resposta do fornecedor vira Expired, nunca é apagado — o
// histórico de bans dele é o dado que justifica trocar de plano ou de fornecedor.
public class Proxy
{
    public Guid Id { get; set; }

    // Deixa a porta aberta para um segundo fornecedor sem migração de dados.
    public string Provider { get; set; } = ProxyProviders.ProxyBr;

    // Identificador no fornecedor (`shortId` na API do ProxyBR).
    public string ShortId { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
    public ProxyKind Kind { get; set; } = ProxyKind.Unknown;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public int? SocksPort { get; set; }
    public string? Username { get; set; }

    // Segredo do fornecedor: nunca sai em DTO e nunca entra em log.
    public string? Password { get; set; }

    public ProxyStatus Status { get; set; } = ProxyStatus.Active;

    // Quantos dispositivos o plano deste proxy permite, quando o fornecedor
    // informa. Nulo = não sabemos, cai no default de config.
    public int? DeviceLimit { get; set; }

    // Ajuste manual pela tela; vence o que veio do fornecedor.
    public int? CapacityOverride { get; set; }

    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public DateTime? LastTestedAt { get; set; }
    public bool? LastTestOk { get; set; }
    public DateTime CreatedAt { get; set; }

    // Capacidade efetiva, em cascata: ajuste manual → limite do fornecedor →
    // default global. O limite de dispositivos é escolhido proxy a proxy na
    // contratação, então isto nunca pode ser constante no código.
    public int CapacityOr(int defaultCapacity) =>
        CapacityOverride ?? DeviceLimit ?? defaultCapacity;

    // Só proxy saudável recebe número novo. Suspeito (bans acumulados) sai da
    // fila sozinho, sem ninguém precisar reparar nele.
    public bool AcceptsNewNumbers => Status == ProxyStatus.Active && LastTestOk != false;
}

public static class ProxyProviders
{
    public const string ProxyBr = "proxybr";
}

public enum ProxyKind
{
    Unknown = 0,
    Ipv4 = 1,
    Ipv6 = 2,
    Isp = 3,
    Residential = 4,
    Mobile = 5,
}

public enum ProxyStatus
{
    Active = 0,

    // Tirado da fila pelo operador, sem deixar de valer para quem já está nele.
    Paused = 1,

    // Bans demais na janela: para de receber número novo automaticamente.
    Suspect = 2,

    // Sumiu do fornecedor (assinatura vencida ou cancelada fora do sistema).
    Expired = 3,

    // Revogado no fornecedor.
    Revoked = 4,

    // A Evolution recusou estas credenciais (`400 Invalid proxy`).
    Failed = 5,
}

public enum ProxyProtocol
{
    Http = 0,
    Socks5 = 1,
}
