using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Api.Features.Metrics;

public record CreateHolidayRequest(DateOnly Date, string Name);

public record HolidayResponse(Guid Id, DateOnly Date, string Name)
{
    public static HolidayResponse From(Holiday holiday) => new(holiday.Id, holiday.Date, holiday.Name);
}

public static class HolidaysEndpoints
{
    public static RouteGroupBuilder MapHolidaysEndpoints(this RouteGroupBuilder group)
    {
        var holidays = group.MapGroup("/holidays");

        holidays.MapPost("/", async (
            CreateHolidayRequest request,
            AppDbContext db,
            ReportCacheVersion cacheVersion,
            DailyMetricsBuilder builder,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["O nome do feriado é obrigatório."]
                });

            var exists = await db.Set<Holiday>().AnyAsync(h => h.Date == request.Date, ct);
            if (exists)
                return Results.Conflict(new { error = "Já existe feriado cadastrado nesta data." });

            var holiday = new Holiday { Id = Guid.NewGuid(), Date = request.Date, Name = request.Name.Trim() };
            db.Add(holiday);
            await db.SaveChangesAsync(ct);
            cacheVersion.Bump();
            // O feriado muda o relógio útil daquele dia e de esperas que o atravessam.
            await builder.MarkRangeDirtyAsync(request.Date.AddDays(-3), request.Date.AddDays(3), ct);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/holidays/{holiday.Id}", HolidayResponse.From(holiday));
        });

        holidays.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var all = await db.Set<Holiday>().AsNoTracking()
                .OrderBy(h => h.Date)
                .Select(h => new HolidayResponse(h.Id, h.Date, h.Name))
                .ToListAsync(ct);

            return Results.Ok(all);
        });

        holidays.MapDelete("/{id:guid}", async (
            Guid id,
            AppDbContext db,
            ReportCacheVersion cacheVersion,
            DailyMetricsBuilder builder,
            CancellationToken ct) =>
        {
            var holiday = await db.Set<Holiday>().FirstOrDefaultAsync(h => h.Id == id, ct);
            if (holiday is null)
                return Results.NotFound();

            var date = holiday.Date;
            db.Remove(holiday);
            await db.SaveChangesAsync(ct);
            cacheVersion.Bump();
            await builder.MarkRangeDirtyAsync(date.AddDays(-3), date.AddDays(3), ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return group;
    }
}
