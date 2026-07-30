namespace MonitorVendas.Api.Features.Ai.Analysis;

// Leitura da conversa feita pela IA. Nunca alimenta métrica: vive nas abas de
// análise da planilha, ao lado do desfecho real, para auditar a etiquetagem.
public class ConversationAiAnalysis
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }

    // Chave do cache: conversa que não recebeu mensagem nova não é reanalisada.
    public int MessageCount { get; set; }
    public DateTime LastMessageAt { get; set; }

    // Leitura anterior não é apagada: vira histórico e permite ver a IA mudando
    // de opinião. Só a corrente alimenta planilha e tela (índice único parcial).
    public bool IsCurrent { get; set; } = true;

    // Código do catálogo de desfechos, ou `Open` para conversa ainda viva.
    public string StatusCode { get; set; } = string.Empty;
    public double StatusConfidence { get; set; }
    public string? StatusEvidence { get; set; }

    public string? LossReason { get; set; }
    public bool AskedForSale { get; set; }
    public bool IgnoredBuyingSignal { get; set; }
    public string? Objections { get; set; }
    public bool ShouldRecontact { get; set; }
    public string? RecontactReason { get; set; }
    public string? SuggestedMessage { get; set; }
    public string? Interest { get; set; }
    public string? Summary { get; set; }
    public string? ConductAlert { get; set; }

    public string Model { get; set; } = string.Empty;
    public decimal CostBrl { get; set; }
    public DateTime AnalyzedAt { get; set; }

    // "Em andamento" não é desfecho — é a ausência dele. Fica embutido porque o
    // catálogo do usuário só cataloga conversas encerradas.
    public const string Open = "open";
}
