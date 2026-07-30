namespace MonitorVendas.Api.Features.Numbers;

public class WhatsappNumber
{
    public Guid Id { get; set; }
    public Guid SellerId { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string InstanceName { get; set; } = string.Empty;
    public NumberStatus Status { get; set; } = NumberStatus.Disconnected;
    public DateTime CreatedAt { get; set; }
}

public enum NumberStatus
{
    Disconnected = 0,
    Active = 1,
    BannedTemporary = 2,
    BannedPermanent = 3
}
