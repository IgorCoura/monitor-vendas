using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Api.Features.Numbers.Warmup;

public static class WarmupEndpoints
{
    public static RouteGroupBuilder MapWarmupEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/warmup", async (WarmupQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.OverviewAsync(ct)));

        // Volta ao dia 1. Limpa pausa e conclusão: reiniciar é começar de novo,
        // não retomar de onde parou.
        group.MapPost("/numbers/{id:guid}/warmup/restart", (Guid id, AppDbContext db, CancellationToken ct) =>
            UpdateAsync(id, db, ct, n =>
            {
                n.WarmupStartedAt = DateTime.UtcNow;
                n.WarmupPausedAt = null;
                n.WarmupCompletedAt = null;
            }));

        group.MapPost("/numbers/{id:guid}/warmup/pause", (Guid id, AppDbContext db, CancellationToken ct) =>
            UpdateAsync(id, db, ct, n => n.WarmupPausedAt ??= DateTime.UtcNow));

        // Retomar empurra o início para frente pelo tempo parado: o número volta
        // no MESMO dia da curva em que parou, em vez de ter envelhecido de graça.
        group.MapPost("/numbers/{id:guid}/warmup/resume", (Guid id, AppDbContext db, CancellationToken ct) =>
            UpdateAsync(id, db, ct, n =>
            {
                if (n.WarmupPausedAt is not { } pausedAt)
                    return;

                if (n.WarmupStartedAt is { } start)
                    n.WarmupStartedAt = start + (DateTime.UtcNow - pausedAt);

                n.WarmupPausedAt = null;
            }));

        // A ÚNICA ação que afrouxa proteção: tira o número da curva antes da
        // hora. A tela pede confirmação, e o motivo fica no log.
        group.MapPost("/numbers/{id:guid}/warmup/complete", async (
            Guid id, AppDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            var result = await UpdateAsync(id, db, ct, n =>
            {
                n.WarmupCompletedAt = DateTime.UtcNow;
                n.WarmupPausedAt = null;
            });

            logger.LogWarning("Aquecimento do número {NumberId} concluído manualmente: o teto progressivo deixa de valer.", id);
            return result;
        });

        return group;
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, AppDbContext db, CancellationToken ct, Action<WhatsappNumber> apply)
    {
        var number = await db.Set<WhatsappNumber>().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (number is null)
            return Results.NotFound();

        apply(number);
        await db.SaveChangesAsync(ct);
        return Results.Ok(NumberResponse.From(number));
    }
}
