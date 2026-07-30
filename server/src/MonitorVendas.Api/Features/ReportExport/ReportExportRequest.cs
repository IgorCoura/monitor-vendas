using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Api.Features.ReportExport;

// O que o usuário escolheu na tela. Listas vazias significam "tudo", para o
// pedido mínimo continuar produzindo um relatório completo.
public sealed record ReportExportRequest(
    DateTime From,
    DateTime To,
    IReadOnlyList<Guid> SellerIds,
    IReadOnlyList<string> Metrics,
    IReadOnlyList<string> Charts,
    bool IncludeNumbers,
    bool IncludeAi,
    // Enviar o áudio manda a voz do cliente para o provedor, e o mascaramento de
    // nome e telefone não alcança isso. Sempre escolha explícita do pedido.
    bool IncludeAudio = false)
{
    public static ReportExportRequest Empty(DateTime from, DateTime to) =>
        new(from, to, [], [], [], true, false);
}

// Uma linha da aba "IA — Conversas". `Divergent` é a coluna que justifica a
// feature: IA e etiqueta discordando é etiquetagem esquecida ou errada.
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

public sealed record AiExportSummary(
    string Model,
    int Analyzed,
    int FromCache,
    int NotAnalyzed,
    decimal CostBrl,
    DateTime GeneratedAt);

public sealed record ReportExportData(
    DateTime From,
    DateTime To,
    IReadOnlyList<RankingEntryDto> Ranking,
    IReadOnlyList<SellerReportDto> SellerReports,
    IReadOnlyList<AiConversationRow> AiRows,
    IReadOnlyList<SellerSynthesis> Syntheses,
    AiExportSummary? AiSummary);
