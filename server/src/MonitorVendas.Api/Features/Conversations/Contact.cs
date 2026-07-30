namespace MonitorVendas.Api.Features.Conversations;

// Cliente do WhatsApp; compartilhado entre números (o mesmo cliente pode falar com N vendedores).
public class Contact
{
    public Guid Id { get; set; }
    public string RemoteJid { get; set; } = string.Empty;
    public string? PushName { get; set; }
    public DateTime CreatedAt { get; set; }
}
