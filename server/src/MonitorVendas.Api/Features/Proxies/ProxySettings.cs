using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Api.Features.Proxies;

// Interruptor operacional "Usar proxies". Mora no banco, não no appsettings:
// precisa ser alterável pela tela sem redeploy e sobreviver a restart.
public class ProxySettings
{
    // Linha única: a chave fixa é o que impede uma segunda linha de aparecer e
    // o sistema passar a ter duas verdades.
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}

public interface IProxySwitch
{
    Task<bool> IsEnabledAsync(AppDbContext db, CancellationToken ct);
}

public sealed class ProxySwitch : IProxySwitch
{
    public async Task<bool> IsEnabledAsync(AppDbContext db, CancellationToken ct)
    {
        var settings = await db.Set<ProxySettings>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == ProxySettings.SingletonId, ct);

        // Sem linha = nunca configurado. Ligado por default: quem instalou a
        // feature quer usá-la.
        return settings?.Enabled ?? true;
    }
}
