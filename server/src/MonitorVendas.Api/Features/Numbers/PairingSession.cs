namespace MonitorVendas.Api.Features.Numbers;

public enum PairingStatus
{
    // QR gerado, esperando alguém escanear.
    AwaitingScan,

    // Conectou, mas o número encontrado exige uma decisão de quem opera
    // (transferir de vendedor, reativar número banido).
    AwaitingConfirmation,

    // Número verificado e vinculado ao vendedor.
    Completed,

    // Recusado por regra (número já conectado, já cadastrado no mesmo vendedor)
    // ou cancelado/expirado. A instância criada para a tentativa é descartada.
    Rejected,
}

// Uma tentativa de conectar um WhatsApp. O número NÃO é digitado: ele é
// descoberto do `wuid` que a Evolution informa ao conectar, e só então vira
// cadastro. É o que impede o cadastro dizer um número e a sessão ser de outro.
public class PairingSession
{
    public Guid Id { get; set; }
    public Guid SellerId { get; set; }

    // Nome opaco (`mv-{guid}`): a instância nasce antes de sabermos o número, e a
    // Evolution não renomeia instância — recriar com o nome "certo" custaria um
    // novo pareamento.
    public string InstanceName { get; set; } = string.Empty;

    public PairingStatus Status { get; set; }

    // `true` enquanto a sessão está viva, NULL quando termina. Com o índice único
    // parcial, é o banco que garante um pareamento por vez em todo o sistema.
    public bool? Active { get; set; }

    // Descobertos na conexão.
    public string? DetectedPhone { get; set; }
    public string? DetectedProfileName { get; set; }

    // O QR fica guardado aqui porque a tela o lê pelo polling da sessão, não pela
    // resposta da criação: a Evolution **regenera** o código a cada ~30 s e manda
    // o novo por `QRCODE_UPDATED`. Servir só o primeiro deixaria na tela um QR já
    // vencido — que o WhatsApp recusa sem dizer por quê.
    public string? QrCode { get; set; }
    public string? QrBase64 { get; set; }

    // Alternativa ao QR para quem abre o painel no próprio celular: o WhatsApp
    // manda este código para o número informado. **O número pedido aqui não vira
    // cadastro** — ele só diz ao WhatsApp para quem mandar o código; o cadastro
    // continua saindo do `wuid` de quem realmente conectou.
    public string? PairingCode { get; set; }

    // Número já cadastrado que esta sessão vai reaproveitar (transferência ou
    // reativação). Nunca se cria registro novo para um número que já existe: o
    // histórico dele ficaria órfão.
    public Guid? ExistingNumberId { get; set; }

    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Início da quarentena: enquanto a sessão não é confirmada, os eventos da
    // instância são descartados. Confirmada, a reconciliação varre desde aqui e
    // traz de volta o que foi descartado.
    public DateTime QuarantineFrom { get; set; }
}
