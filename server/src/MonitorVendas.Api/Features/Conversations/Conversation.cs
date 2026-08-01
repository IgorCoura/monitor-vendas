namespace MonitorVendas.Api.Features.Conversations;

// Conversa derivada por regra: nova quando a mensagem chega após N dias (config) de
// silêncio no par (número, contato). WhatsApp não tem conceito nativo de "ticket".
public class Conversation
{
    public Guid Id { get; set; }
    public Guid WhatsappNumberId { get; set; }

    // Vendedor dono do número quando a conversa aconteceu. É gravado aqui em vez
    // de derivado de WhatsappNumber.SellerId porque o número pode ser transferido:
    // sem este carimbo, a transferência levaria junto todo o passado e o ranking
    // de um mês fechado mudaria depois de fechado.
    public Guid SellerId { get; set; }

    public Guid ContactId { get; set; }
    public bool StartedByContact { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime LastMessageAt { get; set; }
}
