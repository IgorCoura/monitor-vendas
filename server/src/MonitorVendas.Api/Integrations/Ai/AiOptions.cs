namespace MonitorVendas.Api.Integrations.Ai;

public sealed class AiOptions
{
    public const string Section = "Ai";

    public string Provider { get; set; } = "gemini";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-2.5-flash";

    // O raciocínio é cobrado como saída e sai deste mesmo teto: numa conversa
    // curta o modelo gastou 430 tokens pensando para 37 de resposta. Teto baixo
    // trunca o JSON no meio e a análise se perde.
    public int MaxOutputTokens { get; set; } = 3_000;

    // Negativo (default) **não envia** thinkingConfig. Os modelos Gemini 3.x
    // recusam `thinkingBudget` com 400 — não dá para desligar o raciocínio neles.
    // Só use >= 0 em modelo que aceite orçamento de pensamento.
    public int ThinkingBudgetTokens { get; set; } = -1;

    public double Temperature { get; set; } = 0.2;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxAttempts { get; set; } = 3;
    public double RetryBackoffSeconds { get; set; } = 2;

    // O 429 do Gemini diz em quanto tempo tentar de novo (56s no free tier, que
    // limita a 5 chamadas por minuto). Esperar isso é o que faz a exportação
    // terminar em vez de queimar as tentativas em segundos.
    public double MaxRetryDelaySeconds { get; set; } = 90;

    // Teto de espera somada de uma chamada. Sem ele, 3 tentativas de ~60s fazem
    // uma única análise estourar sozinha o prazo da exportação inteira.
    public double MaxTotalRetryWaitSeconds { get; set; } = 70;

    // Free tier estoura com concorrência: 2 já é otimista para 5 RPM.
    public int MaxConcurrency { get; set; } = 2;

    // O preço do provedor é em dólar; o saldo do usuário é em real.
    public decimal UsdBrlRate { get; set; } = 5.40m;

    public Dictionary<string, AiModelPricing> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Estimativa local de tokens, usada para reservar saldo antes de gastar. Não
    // precisa ser exata — precisa ser um teto: o valor real vem do usageMetadata
    // na hora do acerto.
    public double CharsPerToken { get; set; } = 4;
    public double EstimateSafetyFactor { get; set; } = 1.15;

    // Taxa documentada do Gemini para áudio. Serve só à estimativa; o real vem no
    // usageMetadata.
    public double AudioTokensPerSecond { get; set; } = 32;

    // Teto de áudio por conversa. Um único áudio de 30 minutos valeria ~57 mil
    // tokens e comeria o saldo do dia sozinho; o que passar do teto continua na
    // transcrição como marcador.
    public int MaxAudioSecondsPerConversation { get; set; } = 300;
}

public sealed class AiModelPricing
{
    public decimal InputUsdPerMillion { get; set; }
    public decimal OutputUsdPerMillion { get; set; }

    // Áudio tem tarifa própria. Nulo com áudio no pedido é erro alto: cobrar ao
    // preço do texto subfaturaria o saldo sem ninguém perceber.
    public decimal? AudioInputUsdPerMillion { get; set; }
}
