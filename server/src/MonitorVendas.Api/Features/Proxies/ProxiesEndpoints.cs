using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Integrations.ProxyBr;

namespace MonitorVendas.Api.Features.Proxies;

public record ProxySettingsRequest(bool Enabled);

public record AssignProxyRequest(Guid ProxyId);

public record ProxyMoveDto(Guid NumberId, string Phone, string SellerName, string? FromLabel, string ToLabel, bool RestartsSocket);

public record AllocationPreviewDto(IReadOnlyList<ProxyMoveDto> Moves, IReadOnlyList<string> StillWithoutProxy);

public static class ProxiesEndpoints
{
    public static RouteGroupBuilder MapProxiesEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/proxies", async (DateTime? from, DateTime? to, ProxyQueries queries, CancellationToken ct) =>
        {
            var toUtc = (to ?? DateTime.UtcNow).ToUniversalTime();
            var fromUtc = (from ?? toUtc.AddDays(-30)).ToUniversalTime();
            return Results.Ok(await queries.OverviewAsync(fromUtc, toUtc, ct));
        });

        group.MapGet("/proxies/settings", async (AppDbContext db, IProxySwitch proxySwitch, CancellationToken ct) =>
            Results.Ok(new ProxySettingsRequest(await proxySwitch.IsEnabledAsync(db, ct))));

        // Desligar NÃO mexe nas sessões conectadas: tirar o proxy de todas de uma
        // vez reiniciaria todos os sockets juntos. Os números já conectados
        // seguem nos seus proxies até reconectarem.
        group.MapPut("/proxies/settings", async (ProxySettingsRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var settings = await db.Set<ProxySettings>().FirstOrDefaultAsync(s => s.Id == ProxySettings.SingletonId, ct);
            if (settings is null)
            {
                settings = new ProxySettings { Id = ProxySettings.SingletonId };
                db.Add(settings);
            }

            settings.Enabled = request.Enabled;
            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new ProxySettingsRequest(settings.Enabled));
        });

        group.MapPost("/proxies/sync", async (IProxySyncService sync, CancellationToken ct) =>
            Results.Ok(new { synced = await sync.RunOnceAsync(ct) }));

        group.MapPost("/proxies/{id:guid}/test", async (
            Guid id, AppDbContext db, ProxyBrClient client, CancellationToken ct) =>
        {
            var proxy = await db.Set<Proxy>().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (proxy is null)
                return Results.NotFound();

            var ok = await client.TestAsync(proxy.ShortId, ct);
            proxy.LastTestedAt = DateTime.UtcNow;
            proxy.LastTestOk = ok;

            // Teste bem-sucedido reabilita um proxy que a Evolution tinha
            // recusado: o problema pode ter sido momentâneo.
            if (ok == true && proxy.Status == ProxyStatus.Failed)
                proxy.Status = ProxyStatus.Active;

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { tested = ok });
        });

        // Pausar tira da fila de atribuição sem mexer em quem já está no proxy.
        group.MapPost("/proxies/{id:guid}/pause", (Guid id, AppDbContext db, CancellationToken ct) =>
            SetStatusAsync(id, ProxyStatus.Paused, db, ct));

        group.MapPost("/proxies/{id:guid}/resume", (Guid id, AppDbContext db, CancellationToken ct) =>
            SetStatusAsync(id, ProxyStatus.Active, db, ct));

        // Prévia da redistribuição: mostra o plano ANTES de aplicar, porque cada
        // movimento de número conectado custa um restart de socket.
        group.MapGet("/proxies/allocation/preview", async (
            bool? rebalance, ProxyResolver resolver, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await PreviewAsync(rebalance == true, resolver, db, ct)));

        group.MapPost("/proxies/allocation/apply", async (
            bool? rebalance, ProxyResolver resolver, AppDbContext db, CancellationToken ct) =>
        {
            var plan = await resolver.PlanAsync([], rebalance == true, ct);
            var now = DateTime.UtcNow;

            foreach (var move in plan.Assignments)
            {
                // O vínculo é histórico: a atribuição antiga é FECHADA, não
                // sobrescrita, senão o ban de ontem mudaria de proxy junto.
                await db.Set<NumberProxyAssignment>()
                    .Where(a => a.WhatsappNumberId == move.NumberId && a.ReleasedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReleasedAt, now), ct);

                db.Add(new NumberProxyAssignment
                {
                    Id = Guid.NewGuid(),
                    WhatsappNumberId = move.NumberId,
                    ProxyId = move.ProxyId,
                    AssignedAt = now,
                    Reason = move.FromProxyId is null ? ProxyAssignmentReason.Auto : ProxyAssignmentReason.Rebalance,
                });
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { moved = plan.Assignments.Count, withoutProxy = plan.Unassigned.Count });
        });

        group.MapPost("/numbers/{id:guid}/proxy", async (
            Guid id, AssignProxyRequest request, AppDbContext db, CancellationToken ct) =>
        {
            if (!await db.Set<WhatsappNumber>().AnyAsync(n => n.Id == id, ct))
                return Results.NotFound();

            if (!await db.Set<Proxy>().AnyAsync(p => p.Id == request.ProxyId, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["proxyId"] = ["Proxy não encontrado."],
                });

            var now = DateTime.UtcNow;
            await db.Set<NumberProxyAssignment>()
                .Where(a => a.WhatsappNumberId == id && a.ReleasedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReleasedAt, now), ct);

            db.Add(new NumberProxyAssignment
            {
                Id = Guid.NewGuid(),
                WhatsappNumberId = id,
                ProxyId = request.ProxyId,
                AssignedAt = now,
                Reason = ProxyAssignmentReason.Manual,
            });

            await db.SaveChangesAsync(ct);
            return Results.Accepted();
        });

        group.MapDelete("/numbers/{id:guid}/proxy", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var released = await db.Set<NumberProxyAssignment>()
                .Where(a => a.WhatsappNumberId == id && a.ReleasedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReleasedAt, now), ct);

            return released == 0 ? Results.NotFound() : Results.NoContent();
        });

        return group;
    }

    private static async Task<AllocationPreviewDto> PreviewAsync(
        bool rebalance, ProxyResolver resolver, AppDbContext db, CancellationToken ct)
    {
        var plan = await resolver.PlanAsync([], rebalance, ct);

        var labels = await db.Set<Proxy>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Label, ct);
        var numbers = await db.Set<WhatsappNumber>().AsNoTracking()
            .Join(db.Set<Seller>().AsNoTracking(), n => n.SellerId, s => s.Id,
                (n, s) => new { n.Id, n.Phone, n.Status, SellerName = s.Name })
            .ToDictionaryAsync(x => x.Id, ct);

        var moves = plan.Assignments
            .Where(a => numbers.ContainsKey(a.NumberId))
            .Select(a => new ProxyMoveDto(
                a.NumberId,
                numbers[a.NumberId].Phone,
                numbers[a.NumberId].SellerName,
                a.FromProxyId is { } from ? labels.GetValueOrDefault(from) : null,
                labels.GetValueOrDefault(a.ProxyId, "—"),
                // Só número no ar reinicia o socket; o desconectado pega o proxy
                // novo quando voltar.
                numbers[a.NumberId].Status == NumberStatus.Active))
            .ToList();

        var stillWithout = plan.Unassigned
            .Where(numbers.ContainsKey)
            .Select(id => numbers[id].Phone)
            .ToList();

        return new AllocationPreviewDto(moves, stillWithout);
    }

    private static async Task<IResult> SetStatusAsync(Guid id, ProxyStatus status, AppDbContext db, CancellationToken ct)
    {
        var updated = await db.Set<Proxy>()
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, status), ct);

        return updated == 0 ? Results.NotFound() : Results.Ok(new { status = status.ToString() });
    }
}
