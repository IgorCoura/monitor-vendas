using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace MonitorVendas.Tests.Infrastructure;

// Handler HTTP fake que substitui a Evolution API nos testes: grava toda
// requisição recebida e responde com JSON pré-programado por rota.
public sealed class FakeEvolutionHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, (HttpStatusCode, string)> Respond)> _routes = [];

    public IReadOnlyCollection<RecordedRequest> Requests => [.. _requests];

    public void Reset()
    {
        _requests.Clear();
        lock (_routes) _routes.Clear();
    }

    public void When(HttpMethod method, string pathStartsWith, string jsonResponse, HttpStatusCode status = HttpStatusCode.OK)
    {
        lock (_routes)
            _routes.Add((
                req => req.Method == method && req.RequestUri!.AbsolutePath.StartsWith(pathStartsWith, StringComparison.OrdinalIgnoreCase),
                _ => (status, jsonResponse)));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, body));

        (HttpStatusCode, string)? match = null;
        lock (_routes)
        {
            foreach (var route in _routes)
            {
                if (route.Match(request))
                {
                    match = route.Respond(request);
                    break;
                }
            }
        }

        var (status, json) = match ?? (HttpStatusCode.OK, "{}");
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    public sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);
}
