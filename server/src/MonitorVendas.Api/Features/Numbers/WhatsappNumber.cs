namespace MonitorVendas.Api.Features.Numbers;

public class WhatsappNumber
{
    public Guid Id { get; set; }
    public Guid SellerId { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string InstanceName { get; set; } = string.Empty;
    public NumberStatus Status { get; set; } = NumberStatus.Disconnected;
    public DateTime CreatedAt { get; set; }

    // Até quando a reconciliação já varreu este número. É a marca d'água que
    // substitui a janela fixa: uma queda de 5h com lookback de 2h perdia 3h em
    // silêncio. Só avança quando a Evolution respondeu — falha não pula trecho.
    public DateTime? LastReconciledAt { get; set; }
}

public enum NumberStatus
{
    Disconnected = 0,
    Active = 1,
    BannedTemporary = 2,
    BannedPermanent = 3
}
