using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Integrations.Ai;

namespace MonitorVendas.Api.Features.Ai;

public sealed record AiBudgetStatus(
    bool Enabled,
    decimal Limit,
    decimal Committed,
    decimal Available,
    DateTime WindowStart,
    DateTime WindowEnd);

public sealed record AiReservation(Guid Id, decimal EstimatedBrl);

// O saldo é derivado, nunca guardado: gasto fora da janela corrente simplesmente
// deixa de contar. Isso dá o "não acumula" de graça e dispensa job de recarga —
// se a API ficar fora do ar na virada, na volta o saldo já está certo.
public sealed class AiBudget(
    AppDbContext db,
    IOptions<AiBudgetOptions> options,
    IOptions<MetricsOptions> metrics,
    AiCostCalculator calculator)
{
    // Serializa as reservas: duas exportações simultâneas não podem ler o mesmo
    // saldo e furar o teto juntas.
    private const long LockKey = 728_301;

    public decimal MarginPercent => options.Value.MarginPercent;

    public (DateTime Start, DateTime End) CurrentWindow(DateTime nowUtc)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(metrics.Value.TimeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var hours = Math.Clamp(options.Value.WindowHours, 1, 24);

        var startLocal = local.Date.AddHours(local.Hour / hours * hours);
        // A meia-noite sempre corta a janela: com 5h no config, o último bloco do
        // dia é curto, mas o horário de recarga é previsível todo dia.
        var endLocal = startLocal.AddHours(hours);
        var midnight = local.Date.AddDays(1);
        if (endLocal > midnight)
            endLocal = midnight;

        return (ToUtc(startLocal, tz), ToUtc(endLocal, tz));
    }

    public async Task<AiBudgetStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var (start, end) = CurrentWindow(DateTime.UtcNow);
        var committed = await CommittedAsync(start, ct);
        var limit = options.Value.AmountPerWindow;

        return new AiBudgetStatus(options.Value.Enabled, limit, committed, Math.Max(0m, limit - committed), start, end);
    }

    // Devolve null quando o saldo não cobre a estimativa — quem chama não deve
    // sequer montar o prompt.
    public async Task<AiReservation?> TryReserveAsync(string purpose, string model, decimal estimatedBrl, CancellationToken ct = default)
    {
        var settings = options.Value;
        var (start, _) = CurrentWindow(DateTime.UtcNow);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct);

        if (settings.Enabled)
        {
            var committed = await CommittedAsync(start, ct);
            if (committed + estimatedBrl > settings.AmountPerWindow)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }
        }

        var usage = new AiUsage
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            Model = model,
            Status = AiUsageStatus.Reserved,
            EstimatedBrl = estimatedBrl,
            WindowStart = start,
            CreatedAt = DateTime.UtcNow,
        };

        db.Add(usage);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new AiReservation(usage.Id, estimatedBrl);
    }

    // O débito definitivo sai dos tokens que o provedor mediu, com a margem por
    // cima. Sem lock: é uma linha só, e o real vem sempre abaixo da estimativa.
    public async Task<decimal> SettleAsync(
        Guid reservationId,
        string model,
        int inputTokens,
        int outputTokens,
        int inputAudioTokens = 0,
        CancellationToken ct = default)
    {
        var usage = await db.Set<AiUsage>().FirstAsync(u => u.Id == reservationId, ct);
        var raw = calculator.RawCostBrl(model, inputTokens, outputTokens, inputAudioTokens);
        var actual = calculator.WithMargin(raw, options.Value.MarginPercent);

        usage.Status = AiUsageStatus.Settled;
        usage.Model = model;
        usage.ActualBrl = actual;
        usage.InputTokens = inputTokens;
        usage.InputAudioTokens = inputAudioTokens;
        usage.OutputTokens = outputTokens;
        usage.SettledAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return actual;
    }

    public async Task ReleaseAsync(Guid reservationId, CancellationToken ct = default)
    {
        var usage = await db.Set<AiUsage>().FirstAsync(u => u.Id == reservationId, ct);
        usage.Status = AiUsageStatus.Released;
        usage.ActualBrl = 0m;
        usage.SettledAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private Task<decimal> CommittedAsync(DateTime windowStart, CancellationToken ct) =>
        db.Set<AiUsage>().AsNoTracking()
            .Where(u => u.WindowStart == windowStart && u.Status != AiUsageStatus.Released)
            .SumAsync(u => u.Status == AiUsageStatus.Settled ? (u.ActualBrl ?? 0m) : u.EstimatedBrl, ct);

    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }
}
