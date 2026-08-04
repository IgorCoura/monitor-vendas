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

    // Pausa de envio depois de o WhatsApp sinalizar restrição (erro 463): o
    // ContactShareSender não manda nada por este número até vencer. Só trava o
    // que NÓS enviamos — receber e monitorar seguem normais.
    public DateTime? SendingPausedUntil { get; set; }
    public string? SendingPauseReason { get; set; }

    // Cooldown pós-ban: reconectar antes disso exige confirmação explícita,
    // porque a reconexão insistente é o que promove ban temporário a permanente.
    public DateTime? BannedUntil { get; set; }
}

public enum NumberStatus
{
    Disconnected = 0,
    Active = 1,
    BannedTemporary = 2,
    BannedPermanent = 3,

    // Quarentena: a instância conectou com um WhatsApp diferente do cadastrado.
    // Nada entra enquanto o número estiver assim — o histórico de dois números
    // no mesmo cadastro não teria como ser desfeito depois.
    WrongNumber = 4
}
