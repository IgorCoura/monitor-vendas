using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MonitorVendas.Tests.Infrastructure;

// Substitui o provedor de IA nos testes. Responde na ordem em que as respostas
// foram programadas e, esgotada a fila, repete a resposta padrão — cenário com
// N conversas não precisa enfileirar N respostas iguais.
public sealed class FakeAiHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _scripted = new();

    private Func<HttpResponseMessage> _fallback = () => Success("{}");

    private readonly ConcurrentQueue<string> _urls = new();

    public IReadOnlyList<string> Requests => [.. _requests];

    // A URL importa tanto quanto o corpo: caminho errado só apareceria com chave
    // real, gastando uma chamada para descobrir.
    public IReadOnlyList<string> Urls => [.. _urls];

    public int CallCount => _requests.Count;

    public void Reset()
    {
        _requests.Clear();
        _urls.Clear();
        _scripted.Clear();
        _fallback = () => Success("{}");
    }

    // `text` é o conteúdo que o modelo teria gerado (normalmente o JSON do schema).
    public void Enqueue(string text, int inputTokens = 1000, int outputTokens = 200) =>
        _scripted.Enqueue(() => Success(GeminiEnvelope(text, inputTokens, outputTokens)));

    public void EnqueueStatus(HttpStatusCode status, string body = """{"error":{"message":"falhou"}}""") =>
        _scripted.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    public void EnqueueTimeout() =>
        _scripted.Enqueue(() => throw new TaskCanceledException("timeout simulado"));

    public void Always(string text, int inputTokens = 1000, int outputTokens = 200) =>
        _fallback = () => Success(GeminiEnvelope(text, inputTokens, outputTokens));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _urls.Enqueue(request.RequestUri!.ToString());
        _requests.Enqueue(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

        return _scripted.TryDequeue(out var scripted) ? scripted() : _fallback();
    }

    private static HttpResponseMessage Success(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string GeminiEnvelope(string text, int inputTokens, int outputTokens) =>
        JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text } } } } },
            usageMetadata = new { promptTokenCount = inputTokens, candidatesTokenCount = outputTokens },
        });
}
