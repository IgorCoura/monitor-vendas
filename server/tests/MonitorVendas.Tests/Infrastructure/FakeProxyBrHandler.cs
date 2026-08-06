using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace MonitorVendas.Tests.Infrastructure;

// Substitui a API do fornecedor de proxies nos testes. Irmão do
// FakeEvolutionHandler: grava as requisições e responde por rota.
public sealed class FakeProxyBrHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<(HttpStatusCode, string)> Respond)> _routes = [];

    public IReadOnlyCollection<RecordedRequest> Requests => [.. _requests];

    public void Reset()
    {
        _requests.Clear();
        lock (_routes) _routes.Clear();
    }

    public void When(HttpMethod method, string pathContains, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        lock (_routes)
            _routes.Add((
                req => req.Method == method
                    && req.RequestUri!.AbsolutePath.Contains(pathContains, StringComparison.OrdinalIgnoreCase),
                () => (status, json)));
    }

    // Lista de proxies no formato do fornecedor. `devices` entra só quando
    // informado — o campo pode não vir na resposta real, e o sistema tem de
    // funcionar nos dois casos.
    public void WithProxies(params (string ShortId, string Ip, int Port, int? Devices)[] proxies)
    {
        var items = proxies.Select(p =>
            $$"""
            {"short_id":"{{p.ShortId}}","ip":"{{p.Ip}}","port":{{p.Port}},"port_socks5":{{p.Port + 1}},
             "username":"u-{{p.ShortId}}","password":"p-{{p.ShortId}}","type":"proxy-ipv4","status":"active"
             {{(p.Devices is { } d ? $",\"devices\":{d}" : "")}}}
            """);

        When(HttpMethod.Get, "/proxies", $$"""{"data":[{{string.Join(",", items)}}]}""");
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
                    match = route.Respond();
                    break;
                }
            }
        }

        var (status, json) = match ?? (HttpStatusCode.OK, """{"data":[]}""");
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    public sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);
}
