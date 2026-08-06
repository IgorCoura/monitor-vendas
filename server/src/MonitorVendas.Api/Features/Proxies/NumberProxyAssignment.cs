namespace MonitorVendas.Api.Features.Proxies;

// O vínculo número↔proxy é HISTÓRICO, pelo mesmo motivo que Conversation.SellerId
// é carimbado na escrita: o ban de julho tem de continuar contando para o proxy
// que valia em julho. Com uma coluna ProxyId no número, mover um número
// reescreveria o passado e a estatística que justifica trocar de fornecedor
// viraria ficção.
public class NumberProxyAssignment
{
    public Guid Id { get; set; }
    public Guid WhatsappNumberId { get; set; }
    public Guid ProxyId { get; set; }

    public DateTime AssignedAt { get; set; }

    // Null = vigente. O índice único parcial sobre (WhatsappNumberId) WHERE
    // ReleasedAt IS NULL garante um proxy corrente por número — o mesmo idioma
    // de PairingSession.Active e ConversationAiAnalysis.IsCurrent.
    public DateTime? ReleasedAt { get; set; }

    public ProxyAssignmentReason Reason { get; set; }

    // Quando a Evolution confirmou (proxy/set e, se preciso, restart). Nulo =
    // pendente para o aplicador em background.
    public DateTime? AppliedAt { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

public enum ProxyAssignmentReason
{
    Auto = 0,
    Manual = 1,
    Rebalance = 2,
    ProxyUnavailable = 3,
}
