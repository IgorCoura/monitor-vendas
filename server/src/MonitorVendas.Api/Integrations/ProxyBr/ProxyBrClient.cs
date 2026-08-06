using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Features.Proxies;

namespace MonitorVendas.Api.Integrations.ProxyBr;

// Cliente da API do fornecedor de proxies. Ele é catálogo e executor de ações —
// nunca fonte de verdade da distribuição, que é nossa.
public sealed class ProxyBrClient(HttpClient http, IOptions<ProxyBrOptions> options, ILogger<ProxyBrClient> logger)
{
    // 60 req/min POR CONTA, compartilhado entre tokens: sincronizar uma conta
    // grande mais um "testar" do operador estoura o balde. O throttle é nosso
    // porque o fornecedor não perdoa.
    private readonly RequestThrottle _throttle = new(Math.Max(1, options.Value.RequestsPerMinute));

    public async Task<IReadOnlyList<ProxyBrProxy>> ListProxiesAsync(CancellationToken ct = default)
    {
        var all = new List<ProxyBrProxy>();
        var page = 1;

        while (true)
        {
            using var doc = await GetAsync($"proxies?limit=200&page={page}", ct);
            if (doc is null)
                break;

            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            foreach (var item in data.EnumerateArray())
                if (Parse(item) is { } proxy)
                    all.Add(proxy);

            // `meta` pode não vir (resposta não paginada): sem ela, uma página só.
            if (!root.TryGetProperty("meta", out var meta)
                || !meta.TryGetProperty("last_page", out var lastPage)
                || !lastPage.TryGetInt32(out var last)
                || page >= last)
                break;

            page++;
        }

        return all;
    }

    // Testa a conectividade do proxy no fornecedor. Devolve null quando não deu
    // para saber — "não testado" é diferente de "testado e falhou", e só o
    // segundo tira o proxy da fila de atribuição.
    public async Task<bool?> TestAsync(string shortId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"proxies/{Uri.EscapeDataString(shortId)}/test", ct, HttpMethod.Post);
        if (doc is null)
            return null;

        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data))
            root = data;

        foreach (var name in (string[])["success", "ok", "working", "alive"])
            if (root.TryGetProperty(name, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return flag.GetBoolean();

        // Respondeu 200 sem um campo que a gente reconheça: trata como sucesso,
        // porque erro de verdade vem como status HTTP.
        return true;
    }

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct, HttpMethod? method = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await _throttle.WaitAsync(ct);

            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
            var response = await http.SendAsync(request, ct);

            // O fornecedor manda quanto esperar; obedecer é o que faz a
            // sincronização terminar em vez de bater na parede em loop.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(response.Headers.RetryAfter?.Date is { } date
                        ? Math.Max(1, (date - DateTimeOffset.UtcNow).TotalSeconds)
                        : 60);

                logger.LogWarning("ProxyBR respondeu 429 em {Path}: aguardando {Seconds}s.", path, retryAfter.TotalSeconds);
                await Task.Delay(retryAfter, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ProxyBR respondeu {Status} em {Path}.", (int)response.StatusCode, path);
                return null;
            }

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }

        return null;
    }

    // O contrato documenta ip/port/port_socks5/username/password; o resto varia
    // por plano. Tudo aqui é tolerante: campo que não vier simplesmente não
    // preenche, e o default de config cobre.
    private static ProxyBrProxy? Parse(JsonElement item)
    {
        var shortId = Str(item, "short_id") ?? Str(item, "shortId") ?? Str(item, "id");
        var host = Str(item, "ip") ?? Str(item, "host");
        if (shortId is null || host is null)
            return null;

        return new ProxyBrProxy(
            ShortId: shortId,
            Label: Str(item, "label") ?? Str(item, "name") ?? host,
            Host: host,
            Port: Int(item, "port") ?? 0,
            SocksPort: Int(item, "port_socks5") ?? Int(item, "portSocks5"),
            Username: Str(item, "username"),
            Password: Str(item, "password"),
            Kind: KindOf(Str(item, "type") ?? Str(item, "plan_slug") ?? Str(item, "category")),
            // O limite de dispositivos é escolhido na contratação de cada proxy.
            // A coleção documenta `devices` só como ENTRADA da compra, então
            // pode não vir aqui — por isso é `int?` e o default assume.
            DeviceLimit: Int(item, "devices") ?? Int(item, "device_limit") ?? Int(item, "max_devices"),
            Status: Str(item, "status"),
            ExpiresAt: Date(item, "expires_at") ?? Date(item, "expiresAt") ?? Date(item, "due_at"));
    }

    private static ProxyKind KindOf(string? raw) => raw?.ToLowerInvariant() switch
    {
        null => ProxyKind.Unknown,
        var s when s.Contains("ipv6") => ProxyKind.Ipv6,
        var s when s.Contains("ipv4") || s.Contains("datacenter") => ProxyKind.Ipv4,
        var s when s.Contains("isp") => ProxyKind.Isp,
        var s when s.Contains("resid") => ProxyKind.Residential,
        var s when s.Contains("mobile") || s.Contains("movel") => ProxyKind.Mobile,
        _ => ProxyKind.Unknown,
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => null,
        };
    }

    private static DateTime? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;
}

public sealed record ProxyBrProxy(
    string ShortId,
    string Label,
    string Host,
    int Port,
    int? SocksPort,
    string? Username,
    string? Password,
    ProxyKind Kind,
    int? DeviceLimit,
    string? Status,
    DateTime? ExpiresAt);

// Janela deslizante simples: guarda o instante das últimas N requisições e
// espera o suficiente para nunca passar de N por minuto.
internal sealed class RequestThrottle(int perMinute)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTime> _recent = new();

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(1))
                _recent.Dequeue();

            if (_recent.Count >= perMinute)
            {
                var wait = TimeSpan.FromMinutes(1) - (now - _recent.Peek());
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct);
                _recent.Dequeue();
            }

            _recent.Enqueue(DateTime.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }
}
