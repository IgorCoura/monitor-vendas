using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Integrations.Ai;
using MonitorVendas.Api.Integrations.Ai.Gemini;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Ai;

public class AiProviderTests
{
    // US$ 1,00 por milhão de tokens e câmbio 5,00: a conta de custo fica legível.
    private static AiOptions Settings() => new()
    {
        Model = "fake-model",
        UsdBrlRate = 5m,
        MaxOutputTokens = 900,
        MaxAttempts = 1,
        RetryBackoffSeconds = 0,
        Pricing = new Dictionary<string, AiModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["fake-model"] = new() { InputUsdPerMillion = 1m, OutputUsdPerMillion = 1m },
        },
    };

    private static (GeminiProvider Provider, FakeAiHandler Fake) Build(AiOptions? settings = null)
    {
        var fake = new FakeAiHandler();
        var http = new HttpClient(fake) { BaseAddress = new Uri("http://ai.fake/") };
        return (new GeminiProvider(http, Options.Create(settings ?? Settings()), NullLogger<GeminiProvider>.Instance), fake);
    }

    // 1.200 tokens a US$ 1,00/milhão com câmbio 5,00 dão R$ 0,006.
    [Fact]
    public void RawCost_ConvertsTokensToBrl()
    {
        var calculator = new AiCostCalculator(Options.Create(Settings()));

        Assert.Equal(0.006m, calculator.RawCostBrl("fake-model", 1000, 200));
    }

    // A margem entra por cima do custo real — é ela que cobre a variação do câmbio.
    [Fact]
    public void WithMargin_AddsPercentOnTop()
    {
        var calculator = new AiCostCalculator(Options.Create(Settings()));

        Assert.Equal(0.0072m, calculator.WithMargin(0.006m, 20m));
    }

    // Modelo sem preço cadastrado explode: cobrar zero seria gastar sem teto.
    [Fact]
    public void RawCost_WhenModelHasNoPricing_Throws()
    {
        var calculator = new AiCostCalculator(Options.Create(Settings()));

        var ex = Assert.Throws<InvalidOperationException>(() => calculator.RawCostBrl("outro-modelo", 10, 10));
        Assert.Contains("Ai:Pricing", ex.Message);
    }

    // A estimativa é teto: caracteres/4 com fator de segurança mais o máximo de saída.
    [Fact]
    public void Estimate_IsAnUpperBound()
    {
        var calculator = new AiCostCalculator(Options.Create(Settings()));
        var prompt = new string('a', 400);

        Assert.Equal(115, calculator.EstimateInputTokens(prompt));
        // (115 + 900) tokens × US$ 1,00/milhão × 5,00 = R$ 0,005075, mais 20%.
        Assert.Equal(0.00609m, calculator.EstimateBrl("fake-model", prompt, 900, 20m));
    }

    // O endpoint é montado a partir da BaseUrl com o modelo no caminho. O dois
    // pontos de ":generateContent" é o tipo de detalhe que só apareceria com
    // chave real, gastando uma chamada para descobrir que estava errado.
    [Fact]
    public async Task Complete_CallsTheGenerateContentEndpointOfTheModel()
    {
        var (provider, fake) = Build();
        fake.Enqueue("{}");

        await provider.CompleteAsync(new AiRequest("s", "u"));

        Assert.Equal("http://ai.fake/models/fake-model:generateContent", Assert.Single(fake.Urls));
    }

    // A chave vai no header que o Gemini espera — sem ela toda chamada volta 401.
    [Fact]
    public void Setup_SendsTheApiKeyHeader()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:ApiKey"] = "chave-secreta",
            ["Ai:BaseUrl"] = "https://generativelanguage.googleapis.com/v1beta/",
        }).Build());
        services.AddLogging();
        services.AddAiProvider(services.BuildServiceProvider().GetRequiredService<IConfiguration>());

        var http = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GeminiProvider));

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/", http.BaseAddress!.ToString());
        Assert.Equal("chave-secreta", Assert.Single(http.DefaultRequestHeaders.GetValues("x-goog-api-key")));
    }

    // A resposta traz o texto gerado e os tokens medidos pelo próprio provedor.
    [Fact]
    public async Task Complete_ReadsTextAndUsage()
    {
        var (provider, fake) = Build();
        fake.Enqueue("""{"status":"lost"}""", inputTokens: 1500, outputTokens: 120);

        var completion = await provider.CompleteAsync(new AiRequest("sistema", "usuário"));

        Assert.Equal("""{"status":"lost"}""", completion.Text);
        Assert.Equal(1500, completion.InputTokens);
        Assert.Equal(120, completion.OutputTokens);
        Assert.Equal("fake-model", completion.Model);
    }

    // O raciocínio dos modelos 2.5 vem separado, mas é cobrado como saída: soma.
    [Fact]
    public async Task Complete_CountsThinkingTokensAsOutput()
    {
        var (provider, fake) = Build();
        fake.EnqueueStatus(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = "{}" } } } } },
            usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 20, thoughtsTokenCount = 70 },
        }));

        var completion = await provider.CompleteAsync(new AiRequest("sistema", "usuário"));

        Assert.Equal(90, completion.OutputTokens);
    }

    // Erro que impediu a geração não pode debitar saldo.
    [Fact]
    public async Task Complete_WhenRejected_IsNotCharged()
    {
        var (provider, fake) = Build();
        fake.EnqueueStatus(HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => provider.CompleteAsync(new AiRequest("s", "u")));
        Assert.False(ex.MayHaveBeenCharged);
    }

    // A mensagem vai parar numa célula da planilha: sai o texto do erro, não o
    // JSON inteiro que o provedor devolveu.
    [Fact]
    public async Task Complete_WhenRejected_ExtractsTheReadableMessage()
    {
        var (provider, fake) = Build();
        fake.EnqueueStatus(HttpStatusCode.TooManyRequests, """
            {"error":{"code":429,"message":"You exceeded your current quota.\n* Quota: 5 por minuto","status":"RESOURCE_EXHAUSTED"}}
            """);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => provider.CompleteAsync(new AiRequest("s", "u")));

        Assert.Equal("Gemini respondeu 429: You exceeded your current quota.", ex.Message);
    }

    // Timeout depois do envio mantém o débito: o provedor provavelmente gerou e cobrou.
    [Fact]
    public async Task Complete_WhenTimesOut_IsCharged()
    {
        var (provider, fake) = Build();
        fake.EnqueueTimeout();

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => provider.CompleteAsync(new AiRequest("s", "u")));
        Assert.True(ex.MayHaveBeenCharged);
    }

    // O schema pedido vira responseSchema com mimetype JSON — é o que garante
    // resposta parseável e permite trocar de provedor sem mudar quem chama.
    [Fact]
    public async Task Complete_SendsSchemaInTheProviderDialect()
    {
        var (provider, fake) = Build();
        fake.Enqueue("{}");

        await provider.CompleteAsync(new AiRequest("sistema", "usuário", """{"type":"object"}"""));

        using var doc = JsonDocument.Parse(Assert.Single(fake.Requests));
        var config = doc.RootElement.GetProperty("generationConfig");
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        Assert.Equal("object", config.GetProperty("responseSchema").GetProperty("type").GetString());
    }

    // Regressão: `thinkingConfig` só vai quando pedido. Os modelos Gemini 3.x
    // recusam `thinkingBudget` com 400, e mandá-lo por default derrubava toda
    // chamada real (descoberto contra a API de verdade em 30/07/2026).
    [Fact]
    public async Task Complete_ByDefault_DoesNotSendThinkingConfig()
    {
        var (provider, fake) = Build();
        fake.Enqueue("{}");

        await provider.CompleteAsync(new AiRequest("s", "u"));

        using var doc = JsonDocument.Parse(Assert.Single(fake.Requests));
        Assert.False(doc.RootElement.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _));
    }

    // Quem quiser orçamento de pensamento (modelo que aceite) continua podendo.
    [Fact]
    public async Task Complete_WhenBudgetIsConfigured_SendsThinkingConfig()
    {
        var settings = Settings();
        settings.ThinkingBudgetTokens = 0;
        var (provider, fake) = Build(settings);
        fake.Enqueue("{}");

        await provider.CompleteAsync(new AiRequest("s", "u"));

        using var doc = JsonDocument.Parse(Assert.Single(fake.Requests));
        Assert.Equal(0, doc.RootElement.GetProperty("generationConfig")
            .GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
    }

    // Regressão: o raciocínio consome o mesmo teto de saída. Estourado, o JSON vem
    // cortado e o erro precisa dizer o que fazer, não morrer no parser do schema.
    [Fact]
    public async Task Complete_WhenOutputIsTruncated_SaysSo()
    {
        var (provider, fake) = Build();
        fake.EnqueueStatus(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            candidates = new[] { new { finishReason = "MAX_TOKENS", content = new { parts = new[] { new { text = "{\"stat" } } } } },
            usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 2, thoughtsTokenCount = 900 },
        }));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => provider.CompleteAsync(new AiRequest("s", "u")));

        Assert.Contains("MaxOutputTokens", ex.Message);
        Assert.True(ex.MayHaveBeenCharged);
    }

    // Regressão: o 429 do free tier manda esperar ~56s. Voltar em 2s só queima as
    // tentativas — o `retryDelay` do corpo é obedecido.
    [Fact]
    public async Task Complete_OnRateLimit_HonoursTheRetryDelay()
    {
        var settings = Settings();
        settings.MaxAttempts = 2;
        settings.MaxRetryDelaySeconds = 90;
        var (provider, fake) = Build(settings);
        fake.EnqueueStatus(HttpStatusCode.TooManyRequests, """
            {"error":{"code":429,"details":[{"retryDelay":"56.5s"}]}}
            """);
        fake.Enqueue("""{"ok":true}""");

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var task = provider.CompleteAsync(new AiRequest("s", "u"));
        // Não espera de verdade: basta provar que a espera não é o backoff de 0s.
        var finished = await Task.WhenAny(task, Task.Delay(500));
        elapsed.Stop();

        Assert.NotSame(task, finished);
        Assert.Equal(1, fake.CallCount);
    }

    // Regressão: a espera somada de uma chamada tem teto. Sem ele, 3 tentativas de
    // ~60s faziam uma única análise estourar sozinha o prazo da exportação inteira
    // (prazo de 120s virando 202s, medido em 30/07/2026).
    [Fact]
    public async Task Complete_StopsRetryingWhenTheTotalWaitWouldBeTooLong()
    {
        var settings = Settings();
        settings.MaxAttempts = 5;
        settings.MaxRetryDelaySeconds = 90;
        settings.MaxTotalRetryWaitSeconds = 0;
        var (provider, fake) = Build(settings);
        fake.EnqueueStatus(HttpStatusCode.TooManyRequests, """
            {"error":{"code":429,"message":"quota","details":[{"retryDelay":"56s"}]}}
            """);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => provider.CompleteAsync(new AiRequest("s", "u")));

        Assert.Equal(1, fake.CallCount);
        Assert.Contains("429", ex.Message);
        Assert.False(ex.MayHaveBeenCharged);
    }

    // Espera absurda (limite diário) não trava a exportação: cai no backoff normal.
    [Fact]
    public async Task Complete_WhenRetryDelayIsTooLong_FallsBackToBackoff()
    {
        var settings = Settings();
        settings.MaxAttempts = 2;
        settings.MaxRetryDelaySeconds = 10;
        var (provider, fake) = Build(settings);
        fake.EnqueueStatus(HttpStatusCode.TooManyRequests, """
            {"error":{"code":429,"details":[{"retryDelay":"3600s"}]}}
            """);
        fake.Enqueue("""{"ok":true}""");

        var completion = await provider.CompleteAsync(new AiRequest("s", "u"));

        Assert.Equal("""{"ok":true}""", completion.Text);
        Assert.Equal(2, fake.CallCount);
    }

    // 429 é transitório: com tentativas sobrando, tenta de novo antes de desistir.
    [Fact]
    public async Task Complete_RetriesOnRateLimit()
    {
        var settings = Settings();
        settings.MaxAttempts = 2;
        var (provider, fake) = Build(settings);
        fake.EnqueueStatus(HttpStatusCode.TooManyRequests);
        fake.Enqueue("""{"ok":true}""");

        var completion = await provider.CompleteAsync(new AiRequest("s", "u"));

        Assert.Equal("""{"ok":true}""", completion.Text);
        Assert.Equal(2, fake.CallCount);
    }
}
