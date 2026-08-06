namespace MonitorVendas.Api.Features.Warmup;

// Número que participa do aquecimento. Opt-in explícito: entrar é decisão de
// quem opera, nunca automática.
public class WarmupPeer
{
    public Guid Id { get; set; }
    public Guid WhatsappNumberId { get; set; }

    // Jeito de escrever deste número, fixo. Sem isso, os dois lados de um par
    // soariam como a mesma pessoa conversando consigo mesma.
    public WarmupPersona Persona { get; set; }

    // Desde quando está no pool. É daqui que sai o crescimento orgânico do
    // círculo: um colega novo por semana, não quatro no primeiro dia.
    public DateTime JoinedAt { get; set; }

    // Null = participa. Sair não apaga histórico nem arestas — voltar depois
    // reencontra o mesmo círculo, como uma relação que existia antes.
    public DateTime? LeftAt { get; set; }
}

public enum WarmupPersona
{
    // Frases curtas, direto ao ponto.
    Seco = 0,

    // Manda várias mensagens seguidas, se estende.
    Falante = 1,

    // Escreve tudo em minúsculas, abrevia bastante.
    Informal = 2,

    // Usa emoji e pontuação expressiva.
    Expressivo = 3,
}

// Uma relação entre dois números do pool. `PeerAId` é sempre o menor Guid: sem
// essa normalização, o mesmo par entraria duas vezes com os lados trocados.
public class WarmupLink
{
    public Guid Id { get; set; }
    public Guid PeerAId { get; set; }
    public Guid PeerBId { get; set; }
    public WarmupLinkKind Kind { get; set; }

    // Quantas conversas por semana este par costuma ter. Sorteado na criação e
    // desigual de propósito: par uniforme é o que denuncia grafo desenhado.
    public double ConversationsPerWeek { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastConversationAt { get; set; }
}

public enum WarmupLinkKind
{
    // Círculo próximo: fala quase todo dia, estável para sempre.
    Core = 0,

    // Fala a cada uma ou duas semanas, e pode esfriar com o tempo.
    Occasional = 1,

    // Falou uma vez e some por meses. É a cauda que distingue uma rede real de
    // uma malha desenhada.
    Rare = 2,
}

// Uma conversa inteira, gerada de uma vez e tocada ao longo de minutos ou horas.
public class WarmupConversation
{
    public Guid Id { get; set; }
    public Guid? LinkId { get; set; }
    public Guid PeerAId { get; set; }
    public Guid PeerBId { get; set; }

    public string Theme { get; set; } = string.Empty;
    public WarmupConversationStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Arquivar nos DOIS lados: o chat existe no WhatsApp do remetente e no do
    // destinatário, e os dois são nossos.
    public bool ArchivedA { get; set; }
    public bool ArchivedB { get; set; }

    public string? Error { get; set; }
}

public enum WarmupConversationStatus
{
    Scheduled = 0,
    Running = 1,
    Completed = 2,

    // Parou antes do último turno de propósito: conversa que morre no meio é
    // comum, e reciprocidade perfeita é anomalia.
    Abandoned = 3,

    Failed = 4,
}

// Cada mensagem da conversa, com a hora em que deve sair.
public class WarmupTurn
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public int Sequence { get; set; }
    public Guid FromPeerId { get; set; }
    public string Text { get; set; } = string.Empty;

    public DateTime ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string? WaMessageId { get; set; }

    // Ack do WhatsApp. A taxa de entrega do pool sai daqui e alimenta o kill
    // switch — mensagem que não chega é o primeiro sinal de restrição.
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }

    public int Attempts { get; set; }
    public string? Error { get; set; }
}

// Interruptor operacional e estado do kill switch. Linha única, no banco e não
// no appsettings: precisa mudar pela tela sem redeploy.
public class WarmupSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    // Nasce DESLIGADO: a feature não começa a mandar mensagem sozinha depois de
    // um deploy.
    public bool Enabled { get; set; }

    // Quando o kill switch disparou. Enquanto preenchido, nada sai — religar é
    // decisão manual de quem opera.
    public DateTime? HaltedAt { get; set; }
    public string? HaltReason { get; set; }

    // Recuo depois de a geração falhar. Sem ele o agendador pede uma conversa
    // nova a cada passada — 720 chamadas por dia contra um free tier de 20 —, e
    // o aquecimento come sozinho a cota que a análise de conversas precisa.
    public DateTime? GenerationPausedUntil { get; set; }
    public int GenerationFailures { get; set; }
    public string? LastGenerationError { get; set; }

    // Classificação da última falha (ver WarmupGenerationError). "Acabou o saldo
    // em reais" e "acabou a cota do Google" pedem ações opostas de quem opera.
    public string? LastGenerationErrorKind { get; set; }

    public DateTime UpdatedAt { get; set; }
}
