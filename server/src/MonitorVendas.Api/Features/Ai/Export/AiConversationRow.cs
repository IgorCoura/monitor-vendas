namespace MonitorVendas.Api.Features.Ai.Export;

// Uma conversa + a leitura da IA viram uma linha. `Divergent` é a coluna que
// justifica a feature: IA e etiqueta discordando é etiquetagem esquecida ou
// errada.
public sealed record AiConversationRow(
    Guid ConversationId,
    Guid? SellerId,
    // Identidade da leitura: alimenta o hash do cache da síntese e a tela de análises.
    Guid? AnalysisId,
    DateTime? AnalyzedAt,
    string SellerName,
    string SellerNumber,
    string ContactName,
    string ContactPhone,
    DateTime StartedAt,
    DateTime LastMessageAt,
    string? RealOutcome,
    string? AiStatus,
    double? Confidence,
    bool Divergent,
    string? Evidence,
    string? LossReason,
    bool? AskedForSale,
    bool? IgnoredBuyingSignal,
    string? Objections,
    bool? ShouldRecontact,
    string? RecontactReason,
    string? SuggestedMessage,
    string? Interest,
    string? Summary,
    string? ConductAlert,
    string? NotAnalyzedReason);
