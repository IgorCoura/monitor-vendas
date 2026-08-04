namespace MonitorVendas.Api.Features.Conversations;

// Contato que não deve mais receber envio nosso. Além de anti-ban (mandar para
// quem pediu para parar é o caminho curto para a denúncia), é exigência da LGPD:
// o titular pode revogar o consentimento a qualquer momento.
public class ContactOptOut
{
    public Guid Id { get; set; }
    public Guid ContactId { get; set; }
    public OptOutReason Reason { get; set; }

    // O texto que disparou o opt-out, quando houve um. É o que permite explicar
    // ao cliente por que ele parou de receber, e auditar um falso positivo.
    public string? Evidence { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum OptOutReason
{
    // Respondeu SAIR/PARE/etc.
    Requested = 0,

    // Parou de receber o segundo ack: provável bloqueio. Não dá para saber com
    // certeza — o `blocklist.update` do Baileys só informa quem NÓS bloqueamos.
    LikelyBlocked = 1,

    // Marcado à mão por quem opera.
    Manual = 2,
}
