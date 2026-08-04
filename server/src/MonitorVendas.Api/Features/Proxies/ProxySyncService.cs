using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Integrations.ProxyBr;

namespace MonitorVendas.Api.Features.Proxies;

public interface IProxySyncService
{
    Task<int> RunOnceAsync(CancellationToken ct = default);
}

// Espelha o catálogo do fornecedor no nosso banco. O banco do ProxyBR não é
// fonte de verdade — só o nosso: perder o acesso à API dele não pode fazer o
// sistema esquecer a distribuição nem as estatísticas de ban.
public sealed class ProxySyncService(
    IServiceScopeFactory scopeFactory,
    ILogger<ProxySyncService> logger) : IProxySyncService
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<ProxyBrClient>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ProxyOptions>>().Value;

        IReadOnlyList<ProxyBrProxy> remote;
        try
        {
            remote = await client.ListProxiesAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Sincronização de proxies: fornecedor inacessível.");
            return 0;
        }

        var known = await db.Set<Proxy>()
            .Where(p => p.Provider == ProxyProviders.ProxyBr)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reapply = new List<Guid>();

        foreach (var item in remote)
        {
            seen.Add(item.ShortId);
            var proxy = known.FirstOrDefault(p => string.Equals(p.ShortId, item.ShortId, StringComparison.OrdinalIgnoreCase));

            if (proxy is null)
            {
                db.Add(new Proxy
                {
                    Id = Guid.NewGuid(),
                    Provider = ProxyProviders.ProxyBr,
                    ShortId = item.ShortId,
                    Label = item.Label,
                    Kind = item.Kind,
                    Host = item.Host,
                    Port = item.Port,
                    SocksPort = item.SocksPort,
                    Username = item.Username,
                    Password = item.Password,
                    DeviceLimit = item.DeviceLimit,
                    ExpiresAt = item.ExpiresAt,
                    Status = StatusOf(item.Status),
                    LastSyncedAt = now,
                    CreatedAt = now,
                });
                continue;
            }

            // Credencial mudou (IP rotacionado, senha nova): as atribuições
            // vigentes precisam ser reempurradas para a Evolution, senão o
            // número segue tentando sair por um endereço que não existe mais e
            // o Baileys entra em laço de reconexão.
            var credentialsChanged = proxy.Host != item.Host
                || proxy.Port != item.Port
                || proxy.SocksPort != item.SocksPort
                || proxy.Username != item.Username
                || proxy.Password != item.Password;

            proxy.Label = item.Label;
            proxy.Kind = item.Kind;
            proxy.Host = item.Host;
            proxy.Port = item.Port;
            proxy.SocksPort = item.SocksPort;
            proxy.Username = item.Username;
            proxy.Password = item.Password;
            proxy.DeviceLimit = item.DeviceLimit;
            proxy.ExpiresAt = item.ExpiresAt;
            proxy.LastSyncedAt = now;

            // Um proxy que tinha sumido e voltou voltar a valer; Paused e
            // Suspect são decisões NOSSAS e o fornecedor não as desfaz.
            if (proxy.Status is ProxyStatus.Expired or ProxyStatus.Failed)
                proxy.Status = StatusOf(item.Status);

            if (credentialsChanged)
                reapply.Add(proxy.Id);
        }

        // Sumiu da resposta = assinatura encerrada fora do sistema. Vira
        // Expired e NUNCA é apagado: o histórico de bans dele é o dado que
        // justifica trocar de plano ou de fornecedor.
        foreach (var proxy in known.Where(p => !seen.Contains(p.ShortId) && p.Status != ProxyStatus.Expired))
        {
            proxy.Status = ProxyStatus.Expired;
            logger.LogInformation("Proxy {Label} sumiu do fornecedor: marcado como vencido.", proxy.Label);
        }

        await db.SaveChangesAsync(ct);

        if (reapply.Count > 0)
        {
            await db.Set<NumberProxyAssignment>()
                .Where(a => a.ReleasedAt == null && reapply.Contains(a.ProxyId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.AppliedAt, (DateTime?)null)
                    .SetProperty(a => a.Attempts, 0)
                    .SetProperty(a => a.Error, (string?)null), ct);
        }

        await MarkSuspectAsync(db, options, ct);
        return remote.Count;
    }

    // Proxy que acumulou bans na janela sai da fila de atribuição sozinho, sem
    // ninguém precisar reparar nele. Não mexe em quem já está lá: mover número
    // custa restart de socket e é decisão humana.
    private static async Task MarkSuspectAsync(AppDbContext db, ProxyOptions options, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, options.SuspectWindowDays));
        var threshold = Math.Max(1, options.SuspectBansPerWindow);

        var bans = await db.Set<NumberStatusEvent>().AsNoTracking()
            .Where(e => e.OccurredAt >= since && e.StatusReason == 403)
            .Join(db.Set<NumberProxyAssignment>().AsNoTracking(),
                e => e.WhatsappNumberId,
                a => a.WhatsappNumberId,
                (e, a) => new { e.OccurredAt, a.ProxyId, a.AssignedAt, a.ReleasedAt })
            .Where(x => x.AssignedAt <= x.OccurredAt && (x.ReleasedAt == null || x.OccurredAt < x.ReleasedAt))
            .GroupBy(x => x.ProxyId)
            .Select(g => new { ProxyId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var suspect = bans.Where(b => b.Count >= threshold).Select(b => b.ProxyId).ToList();
        if (suspect.Count == 0)
            return;

        await db.Set<Proxy>()
            .Where(p => suspect.Contains(p.Id) && p.Status == ProxyStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ProxyStatus.Suspect), ct);
    }

    private static ProxyStatus StatusOf(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "active" or "ativo" or "running" => ProxyStatus.Active,
        "revoked" or "revogado" or "cancelled" or "canceled" => ProxyStatus.Revoked,
        "expired" or "vencido" or "suspended" => ProxyStatus.Expired,
        _ => ProxyStatus.Active,
    };
}

public sealed class ProxySyncBackgroundService(
    IProxySyncService sync,
    IOptions<ProxyBrOptions> options,
    ILogger<ProxySyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.SyncIntervalMinutes));

        // Uma passada antes do primeiro delay: subir a API já sincroniza, como
        // na reconciliação.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sync.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no laço de sincronização de proxies.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
