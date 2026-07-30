namespace MonitorVendas.Api.Features.Sellers;

public record CreateSellerRequest(string Name);

public record UpdateSellerRequest(string Name, bool Active);

public record SellerResponse(Guid Id, string Name, bool Active, DateTime CreatedAt)
{
    public static SellerResponse From(Seller seller) =>
        new(seller.Id, seller.Name, seller.Active, seller.CreatedAt);
}
