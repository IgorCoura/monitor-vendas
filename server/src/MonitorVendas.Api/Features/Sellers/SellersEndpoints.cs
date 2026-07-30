using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Api.Features.Sellers;

public static class SellersEndpoints
{
    public static RouteGroupBuilder MapSellersEndpoints(this RouteGroupBuilder group)
    {
        var sellers = group.MapGroup("/sellers");

        sellers.MapPost("/", async (CreateSellerRequest request, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["O nome do vendedor é obrigatório."]
                });

            var seller = new Seller
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Active = true,
                CreatedAt = DateTime.UtcNow
            };

            db.Add(seller);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/sellers/{seller.Id}", SellerResponse.From(seller));
        });

        sellers.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var all = await db.Set<Seller>().AsNoTracking()
                .OrderBy(s => s.Name)
                .Select(s => new SellerResponse(s.Id, s.Name, s.Active, s.CreatedAt))
                .ToListAsync(ct);

            return Results.Ok(all);
        });

        sellers.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var seller = await db.Set<Seller>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            return seller is null ? Results.NotFound() : Results.Ok(SellerResponse.From(seller));
        });

        sellers.MapPut("/{id:guid}", async (Guid id, UpdateSellerRequest request, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["O nome do vendedor é obrigatório."]
                });

            var seller = await db.Set<Seller>().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (seller is null)
                return Results.NotFound();

            seller.Name = request.Name.Trim();
            seller.Active = request.Active;
            await db.SaveChangesAsync(ct);

            return Results.Ok(SellerResponse.From(seller));
        });

        return group;
    }
}
