namespace MonitorVendas.Api.Features.Contacts;

public sealed class ContactShareOptions
{
    public const string Section = "ContactShare";

    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 5;

    // Rajada de mensagens pelo mesmo número é o padrão que o WhatsApp mais pune.
    public int DelayBetweenMessagesSeconds { get; set; } = 5;

    // Limite real do WhatsApp é ~4096 caracteres; a folga cobre emoji (que ocupa
    // mais de um caractere na contagem do servidor deles).
    public int MaxCharsPerMessage { get; set; } = 3500;

    // Teto por envio: acima disso a API recusa e pede filtro mais estreito, em
    // vez de queimar o número.
    public int MaxMessagesPerShare { get; set; } = 20;
    public int MaxAttempts { get; set; } = 5;
}
