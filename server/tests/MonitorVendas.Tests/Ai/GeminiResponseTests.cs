using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Integrations.Ai;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Ai;

// A leitura da resposta do Gemini decide quanto se cobra e se a análise vale.
// Formato inesperado aqui vira gasto sem leitura, ou leitura sem cobrança.
public class GeminiResponseTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly AiRequest Request = new("sistema", "usuário", null, 500);

    private async Task<AiCompletion> CompleteAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAiProvider>().CompleteAsync(Request);
    }

    private async Task<AiProviderException> FailAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IAiProvider>();
        return await Assert.ThrowsAsync<AiProviderException>(() => provider.CompleteAsync(Request));
    }

    private static string Envelope(object body) => JsonSerializer.Serialize(body);

    // Resposta normal: texto extraído e tokens de entrada/saída lidos.
    [Fact]
    public async Task Complete_ReadsTextAndTokens()
    {
        FakeAi.Enqueue("resposta do modelo", inputTokens: 1200, outputTokens: 300);

        var completion = await CompleteAsync();

        Assert.Equal("resposta do modelo", completion.Text);
        Assert.Equal(1200, completion.InputTokens);
        Assert.Equal(300, completion.OutputTokens);
        Assert.Equal(0, completion.InputAudioTokens);
    }

    // O `promptTokensDetails` separa a entrada por modalidade: sem isso o áudio
    // seria cobrado ao preço do texto e o saldo subfaturaria em silêncio.
    [Fact]
    public async Task Complete_SplitsAudioTokensFromText()
    {
        FakeAi.EnqueueStatus(HttpStatusCode.OK, Envelope(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = "ok" } } } } },
            usageMetadata = new
            {
                promptTokenCount = 5000,
                candidatesTokenCount = 200,
                promptTokensDetails = new object[]
                {
                    new { modality = "TEXT", tokenCount = 1160 },
                    new { modality = "AUDIO", tokenCount = 3840 },
                },
            },
        }));

        var completion = await CompleteAsync();

        Assert.Equal(3840, completion.InputAudioTokens);
        Assert.Equal(5000, completion.InputTokens);
    }

    // Resposta truncada pelo teto de saída vira erro explícito pedindo para
    // aumentar o teto — e mantém o consumo, porque o provedor já cobrou.
    [Fact]
    public async Task Complete_WhenTruncatedByMaxTokens_ExplainsWhat()
    {
        FakeAi.EnqueueStatus(HttpStatusCode.OK, Envelope(new
        {
            candidates = new[] { new { finishReason = "MAX_TOKENS", content = new { parts = new[] { new { text = "{\"sta" } } } } },
            usageMetadata = new { promptTokenCount = 100, candidatesTokenCount = 500 },
        }));

        var error = await FailAsync();

        Assert.Contains("MaxOutputTokens", error.Message);
        Assert.True(error.MayHaveBeenCharged);
        Assert.Equal(500, error.Usage!.OutputTokens);
    }

    // Resposta sem texto (bloqueio de conteúdo, candidato vazio) também mantém o
    // débito: houve chamada do outro lado.
    [Fact]
    public async Task Complete_WithoutText_FailsKeepingTheCharge()
    {
        FakeAi.EnqueueStatus(HttpStatusCode.OK, Envelope(new
        {
            candidates = new[] { new { content = new { parts = Array.Empty<object>() } } },
            usageMetadata = new { promptTokenCount = 80, candidatesTokenCount = 0 },
        }));

        var error = await FailAsync();

        Assert.Contains("sem texto", error.Message);
        Assert.True(error.MayHaveBeenCharged);
    }

    // Erro do provedor sai com a mensagem dele, não com um JSON cru na tela.
    [Fact]
    public async Task Complete_OnProviderError_SurfacesTheMessage()
    {
        FakeAi.EnqueueStatus(HttpStatusCode.BadRequest,
            """{"error":{"code":400,"message":"API key not valid.\nMais detalhes aqui."}}""");

        var error = await FailAsync();

        Assert.Contains("API key not valid.", error.Message);
        // Só a primeira linha: o resto é ruído para quem lê na tela.
        Assert.DoesNotContain("Mais detalhes", error.Message);
    }

    // Corpo de erro que não é JSON não pode derrubar o tratamento do erro.
    [Fact]
    public async Task Complete_OnNonJsonError_StillFailsCleanly()
    {
        FakeAi.EnqueueStatus(HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>");

        var error = await FailAsync();

        Assert.Contains("502", error.Message);
    }

    // 4xx que não é de cota não é retentado: insistir só queima tempo.
    [Fact]
    public async Task Complete_OnClientError_DoesNotRetry()
    {
        FakeAi.EnqueueStatus(HttpStatusCode.Forbidden, """{"error":{"message":"sem permissão"}}""");

        await FailAsync();

        Assert.Equal(1, FakeAi.CallCount);
    }

    // A chave vai na URL e o modelo no caminho: caminho errado só apareceria com
    // chave real, gastando uma chamada para descobrir.
    [Fact]
    public async Task Complete_CallsTheGenerateContentEndpoint()
    {
        FakeAi.Enqueue("ok");

        await CompleteAsync();

        var url = Assert.Single(FakeAi.Urls);
        Assert.Contains("models/fake-model:generateContent", url);
    }

    // Falha de rede é retentada: a chamada não chegou do outro lado, então repetir
    // não cobra duas vezes — e é o erro mais comum em free tier.
    [Fact]
    public async Task Complete_OnNetworkFailure_RetriesAndSucceeds()
    {
        using var factory = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Ai:MaxAttempts", "2");
            b.UseSetting("Ai:RetryBackoffSeconds", "0");
        });

        FakeAi.EnqueueNetworkFailure();
        FakeAi.Enqueue("veio na segunda");

        using var scope = factory.Services.CreateScope();
        var completion = await scope.ServiceProvider.GetRequiredService<IAiProvider>().CompleteAsync(Request);

        Assert.Equal("veio na segunda", completion.Text);
        Assert.Equal(2, FakeAi.CallCount);
    }

    // Esgotadas as tentativas, o erro diz que não se falou com o provedor e
    // **não** mantém o débito: sem chamada não há cobrança.
    [Fact]
    public async Task Complete_WhenTheNetworkNeverComesBack_FailsWithoutCharging()
    {
        FakeAi.EnqueueNetworkFailure();

        var error = await FailAsync();

        Assert.Contains("Gemini", error.Message);
        Assert.False(error.MayHaveBeenCharged);
    }

    // O 429 traz a espera no header padrão (além do `retryDelay` do corpo).
    // Obedecê-la é o que faz a rodada terminar em vez de bater na cota de novo.
    [Fact]
    public async Task Complete_OnRateLimit_ObeysTheRetryAfterHeader()
    {
        using var factory = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Ai:MaxAttempts", "2");
            b.UseSetting("Ai:RetryBackoffSeconds", "0");
        });

        FakeAi.EnqueueRetryAfter(0);
        FakeAi.Enqueue("depois da cota");

        using var scope = factory.Services.CreateScope();
        var completion = await scope.ServiceProvider.GetRequiredService<IAiProvider>().CompleteAsync(Request);

        Assert.Equal("depois da cota", completion.Text);
    }

    // Resposta sem candidato nenhum (bloqueio de segurança do provedor) é erro de
    // leitura, não análise vazia passando por boa.
    [Fact]
    public async Task Complete_WithoutCandidates_Fails()
    {
        FakeAi.EnqueueStatus(HttpStatusCode.OK, Envelope(new
        {
            promptFeedback = new { blockReason = "SAFETY" },
            usageMetadata = new { promptTokenCount = 50, candidatesTokenCount = 0 },
        }));

        var error = await FailAsync();

        Assert.Contains("sem texto", error.Message);
    }

    // Candidato sem `content`/`parts` é a outra forma do mesmo bloqueio.
    [Fact]
    public async Task Complete_WithCandidateWithoutContent_Fails()
    {
        FakeAi.EnqueueStatus(HttpStatusCode.OK, Envelope(new
        {
            candidates = new[] { new { finishReason = "SAFETY" } },
            usageMetadata = new { promptTokenCount = 50, candidatesTokenCount = 0 },
        }));

        await FailAsync();
    }

    // Erro em JSON que não tem o campo `error` não pode virar mensagem vazia na
    // tela: sobra o status para quem for investigar.
    [Fact]
    public async Task Complete_OnErrorWithoutMessage_StillSaysSomething()
    {
        FakeAi.EnqueueStatus(HttpStatusCode.ServiceUnavailable, """{"detail":"indisponível"}""");

        var error = await FailAsync();

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    // O schema pedido vira responseSchema + responseMimeType: é o que fecha o
    // enum de status contra injeção de prompt.
    [Fact]
    public async Task Complete_WithSchema_AsksForStructuredJson()
    {
        FakeAi.Enqueue("{}");

        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAiProvider>()
                .CompleteAsync(new AiRequest("s", "u", """{"type":"object"}""", 100));
        }

        var body = Assert.Single(FakeAi.Requests);
        Assert.Contains("responseSchema", body);
        Assert.Contains("application/json", body);
    }
}
