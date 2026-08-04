namespace MonitorVendas.Api.Features.Warmup;

public sealed class WarmupOptions
{
    public const string Section = "Warmup";

    // Master switch de infraestrutura (desliga os background services nos
    // testes). O liga/desliga operacional é o interruptor da tela.
    public bool Enabled { get; set; } = true;

    // Meta de mensagens por número por dia, sorteada nesta faixa a cada dia. O
    // aquecimento completa só o que o tráfego real não cobriu — número que
    // conversou muito com aluno recebe pouco ou nada.
    public int MinDailyMessages { get; set; } = 20;
    public int MaxDailyMessages { get; set; } = 40;

    // Teto por par por dia. É ele que capa a meta quando o pool é pequeno: com
    // 4 números cada um tem 3 peers, e sem este limite seriam 10 mensagens/dia
    // com o mesmo colega, todo dia.
    public int MaxMessagesPerPairPerDay { get; set; } = 6;

    // Tamanho final do círculo. Não é atingido de uma vez: um colega novo por
    // semana desde a entrada no pool.
    public int CoreCircleSize { get; set; } = 3;
    public int OccasionalCircleSize { get; set; } = 4;

    // Conversas por semana de cada camada, sorteadas nestas faixas.
    public double CoreConversationsPerWeekMin { get; set; } = 4;
    public double CoreConversationsPerWeekMax { get; set; } = 7;
    public double OccasionalConversationsPerWeekMin { get; set; } = 0.5;
    public double OccasionalConversationsPerWeekMax { get; set; } = 1.5;

    // Chance de, num ciclo, um par que nunca falou trocar algumas mensagens e
    // sumir por meses. É a cauda do grafo.
    public double RareConversationChance { get; set; } = 0.05;

    public int MinTurnsPerConversation { get; set; } = 3;
    public int MaxTurnsPerConversation { get; set; } = 7;

    // Parte das conversas que morre antes do último turno, e parte das
    // mensagens que fica sem resposta. Reciprocidade perfeita é anomalia.
    public double AbandonChance { get; set; } = 0.25;

    // Latência entre turnos: log-normal, mediana perto do mínimo e cauda longa.
    public int MinTurnGapSeconds { get; set; } = 40;
    public int MaxTurnGapSeconds { get; set; } = 5400;

    // Janela de envio. O expediente vem do BusinessHoursCalendar (com feriados);
    // esta é a cauda da noite, porque colega manda mensagem depois do trabalho.
    public int EveningUntilHour { get; set; } = 21;

    // Fim de semana existe, mas bem reduzido.
    public double WeekendFactor { get; set; } = 0.25;

    // Kill switch: abaixo desta taxa de entrega no pool, para tudo.
    public double MinDeliveryRate { get; set; } = 0.60;
    public int DeliverySampleMinimum { get; set; } = 20;

    public int SchedulerIntervalSeconds { get; set; } = 120;
    public int ExecutorIntervalSeconds { get; set; } = 30;
    public int MaxAttemptsPerTurn { get; set; } = 3;

    // Temas sorteados para as conversas. Do ramo (cursos de pós e licenciatura)
    // e da vida de qualquer trabalho — os banais são os mais seguros.
    public List<string> Themes { get; set; } =
    [
        "uma matrícula que travou no sistema",
        "um aluno pedindo prazo maior para o TCC",
        "o polo que ligou perguntando de documentação",
        "material didático que não chegou para uma turma",
        "dúvida de um aluno sobre mensalidade atrasada",
        "uma turma de licenciatura que vai abrir no mês que vem",
        "a coordenação pedindo um relatório",
        "o sistema de matrícula fora do ar",
        "um aluno que sumiu e voltou a responder",
        "certificado de conclusão que demorou a sair",
        "combinar o almoço",
        "trânsito na volta para casa",
        "o café que acabou no escritório",
        "planos para o fim de semana",
        "a chuva que alagou a rua",
        "um jogo que passou ontem",
    ];
}
