using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MonitorVendas.Api.Features.Metrics;

// Versão da configuração que afeta o cálculo (hoje: feriados). Entra na chave do
// cache, então cadastrar/remover feriado invalida tudo na hora em vez de esperar
// o TTL expirar.
public sealed class ReportCacheVersion
{
    private int _version;

    public int Current => Volatile.Read(ref _version);

    public void Bump() => Interlocked.Increment(ref _version);
}

// Cache de resultado de relatório: várias abas/usuários pedindo o mesmo painel no
// mesmo minuto custam um cálculo só. `Metrics:CacheSeconds = 0` desliga.
public sealed class ReportCache(
    IMemoryCache cache,
    ReportCacheVersion version,
    IOptions<MetricsOptions> options)
{
    public async Task<T?> GetOrCreateAsync<T>(string key, bool bypass, Func<Task<T?>> factory)
        where T : class
    {
        var ttlSeconds = options.Value.CacheSeconds;
        if (ttlSeconds <= 0)
            return await factory();

        var fullKey = $"report:v{version.Current}:{key}";

        if (!bypass && cache.TryGetValue(fullKey, out T? cached) && cached is not null)
            return cached;

        var value = await factory();

        // Resposta vazia (vendedor inexistente) não é cacheada: o caso é raro e
        // guardar null só atrasaria a percepção de um cadastro novo.
        if (value is not null)
            cache.Set(fullKey, value, TimeSpan.FromSeconds(ttlSeconds));

        return value;
    }
}
