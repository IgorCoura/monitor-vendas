namespace MonitorVendas.Api.Features.Numbers;

public record CreateNumberRequest(string Phone);

public record QrCodeDto(string? Code, string? Base64, string? PairingCode);

public record NumberResponse(Guid Id, Guid SellerId, string Phone, string InstanceName, string Status, DateTime CreatedAt)
{
    public static NumberResponse From(WhatsappNumber number) =>
        new(number.Id, number.SellerId, number.Phone, number.InstanceName, number.Status.ToString(), number.CreatedAt);
}

public record CreateNumberResponse(NumberResponse Number, QrCodeDto? Qr);
