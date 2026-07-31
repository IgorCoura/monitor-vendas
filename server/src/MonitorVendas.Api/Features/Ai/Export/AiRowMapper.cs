using MonitorVendas.Api.Features.Ai.Analysis;

namespace MonitorVendas.Api.Features.Ai.Export;

// Uma conversa + a leitura da IA viram uma linha. A regra da divergência mora
// só aqui: a planilha, a tela e a síntese precisam concordar sobre o que é
// "IA discorda da etiqueta".
public static class AiRowMapper
{
    public static AiConversationRow ToRow(
        ConversationContext conversation,
        ConversationAiAnalysis? analysis,
        IReadOnlyDictionary<string, string> typeNames,
        string? notAnalyzedReason)
    {
        var realCode = conversation.RealOutcomeCode;

        // "Em andamento" é ausência de desfecho: comparar com a etiqueta exige
        // tratá-lo como nulo, senão toda conversa viva pareceria divergente.
        var aiCode = analysis?.StatusCode == ConversationAiAnalysis.Open ? null : analysis?.StatusCode;

        return new AiConversationRow(
            conversation.ConversationId,
            conversation.SellerId,
            analysis?.Id,
            analysis?.AnalyzedAt,
            conversation.SellerName,
            conversation.SellerNumber,
            conversation.ContactName,
            conversation.ContactPhone,
            conversation.StartedAt,
            conversation.LastMessageAt,
            realCode is null ? null : typeNames.GetValueOrDefault(realCode, realCode),
            analysis is null
                ? null
                : aiCode is null ? "Em andamento" : typeNames.GetValueOrDefault(aiCode, aiCode),
            analysis?.StatusConfidence,
            analysis is not null && !string.Equals(aiCode, realCode, StringComparison.OrdinalIgnoreCase),
            analysis?.StatusEvidence,
            analysis?.LossReason,
            analysis?.AskedForSale,
            analysis?.IgnoredBuyingSignal,
            analysis?.Objections,
            analysis?.ShouldRecontact,
            analysis?.RecontactReason,
            analysis?.SuggestedMessage,
            analysis?.Interest,
            analysis?.Summary,
            analysis?.ConductAlert,
            analysis is not null ? null : notAnalyzedReason);
    }
}
