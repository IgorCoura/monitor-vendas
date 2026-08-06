using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Integrations.Evolution;

namespace MonitorVendas.Api.Features.Proxies;

public interface IProxyApplier
{
    Task<int> ProcessPendingAsync(CancellationToken ct = default);
}

// Empurra para a Evolution as atribuições que ainda não foram aplicadas. O
// `proxy/set` só grava: o agent do Baileys é fixado na criação do socket, então
// número conectado precisa de um restart para passar a sair pelo IP novo.
public sealed class ProxyApplierService(
    IServiceScopeFactory scopeFactory,
    IOptions<ProxyOptions> options,
    ILogger<ProxyApplierService> logger) : IProxyApplier
{
    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evolution = scope.ServiceProvider.GetRequiredService<EvolutionApiClient>();
        var resolver = scope.ServiceProvider.GetRequiredService<ProxyResolver>();
        var proxySwitch = scope.ServiceProvider.GetRequiredService<IProxySwitch>();

        // Desligado o interruptor, o aplicador para. As sessões já conectadas
        // NÃO são mexidas: remover o proxy de todas de uma vez reiniciaria todos
        // os sockets juntos, que é exatamente a mudança brusca e correlacionada
        // que esta feature existe para evitar.
        if (!await proxySwitch.IsEnabledAsync(db, ct))
            return 0;

        var maxAttempts = Math.Max(1, options.Value.MaxAttempts);

        var pending = await db.Set<NumberProxyAssignment>()
            .Where(a => a.ReleasedAt == null && a.AppliedAt == null && a.Attempts < maxAttempts)
            .OrderBy(a => a.AssignedAt)
            .ToListAsync(ct);

        var applied = 0;

        foreach (var assignment in pending)
        {
            var number = await db.Set<WhatsappNumber>().AsNoTracking()
                .FirstOrDefaultAsync(n => n.Id == assignment.WhatsappNumberId, ct);

            if (number is null)
            {
                assignment.ReleasedAt = DateTime.UtcNow;
                continue;
            }

            var credentials = await resolver.CredentialsForAsync(assignment.ProxyId, ct);

            // Uma tentativa por passada, sempre: repescar na mesma passada
            // queimaria as tentativas todas sem intervalo nenhum. Esse bug já
            // apareceu duas vezes neste projeto.
            assignment.Attempts++;

            if (!await evolution.SetProxyAsync(number.InstanceName, credentials, ct))
            {
                assignment.Error = "A Evolution não aceitou a troca de proxy.";
                continue;
            }

            // Só reinicia quem está no ar: o socket de um número desconectado
            // vai nascer com o proxy novo quando ele reconectar.
            if (number.Status == NumberStatus.Active && !await evolution.RestartAsync(number.InstanceName, ct))
            {
                assignment.Error = "Proxy gravado, mas a instância não reiniciou — segue no IP antigo até reconectar.";
                logger.LogWarning("Proxy aplicado em {Phone} sem restart confirmado.", number.Phone);
            }
            else
            {
                assignment.Error = null;
            }

            assignment.AppliedAt = DateTime.UtcNow;
            applied++;
        }

        await db.SaveChangesAsync(ct);
        return applied;
    }
}

public sealed class ProxyApplierBackgroundService(
    IProxyApplier applier,
    IOptions<ProxyOptions> options,
    ILogger<ProxyApplierBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.ApplierIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await applier.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no laço de aplicação de proxies.");
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
