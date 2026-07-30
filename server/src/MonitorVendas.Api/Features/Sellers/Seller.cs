namespace MonitorVendas.Api.Features.Sellers;

public class Seller
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
