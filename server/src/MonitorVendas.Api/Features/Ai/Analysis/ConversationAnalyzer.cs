using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Integrations.Ai;

namespace MonitorVendas.Api.Features.Ai.Analysis;

public sealed record ConversationAnalysisInput(
    Guid ConversationId,
    int MessageCount,
    DateTime LastMessageAt,
    string Transcript,
    bool AllowOpen,
    // A tela de análises tem um botão que refaz a leitura mesmo sem a conversa
    // ter mudado — é o único caminho que ignora o cache de propósito.
    bool Force = false);

public enum AnalysisResultKind
{
    Cached,
    Analyzed,
    NoBudget,
    Failed
}

public sealed record AnalysisOutcome(AnalysisResultKind Kind, ConversationAiAnalysis? Analysis, string? Error);

public sealed class ConversationAnalyzer(
    AppDbContext db,
    IAiProvider provider,
    AiBudget budget,
    AiCostCalculator calculator,
    IOptions<AiOptions> options,
    ILogger<ConversationAnalyzer> logger)
{
    private const string Purpose = "conversation-analysis";
    private const int MaxParseAttempts = 2;

    public async Task<AnalysisOutcome> AnalyzeAsync(
        ConversationAnalysisInput input,
        IReadOnlyList<OutcomeChoice> outcomes,
        CancellationToken ct = default)
    {
        var existing = await db.Set<ConversationAiAnalysis>()
            .FirstOrDefaultAsync(a => a.ConversationId == input.ConversationId && a.IsCurrent, ct);

        // Conversa que não recebeu mensagem nova desde a última leitura não é
        // reanalisada: é a economia que torna reexportar o mesmo período grátis.
        if (!input.Force &&
            existing is not null &&
            existing.MessageCount == input.MessageCount &&
            existing.LastMessageAt == input.LastMessageAt)
            return new AnalysisOutcome(AnalysisResultKind.Cached, existing, null);

        var settings = options.Value;
        var schema = AiAnalysisSchema.BuildSchema(outcomes, input.AllowOpen);
        var userPrompt = AiAnalysisSchema.BuildUserPrompt(outcomes, input.AllowOpen, input.Transcript);
        var request = new AiRequest(AiAnalysisSchema.SystemPrompt, userPrompt, schema, settings.MaxOutputTokens);

        var cost = 0m;
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxParseAttempts; attempt++)
        {
            var estimate = calculator.EstimateBrl(
                settings.Model,
                AiAnalysisSchema.SystemPrompt + userPrompt,
                settings.MaxOutputTokens,
                budget.MarginPercent);

            var reservation = await budget.TryReserveAsync(Purpose, settings.Model, estimate, ct);
            if (reservation is null)
                return new AnalysisOutcome(AnalysisResultKind.NoBudget, null, "Saldo de IA insuficiente.");

            AiCompletion completion;
            try
            {
                completion = await provider.CompleteAsync(request, ct);
            }
            catch (AiProviderException ex)
            {
                await CloseReservationAsync(reservation.Id, ex, ct);
                logger.LogWarning(ex, "Falha ao analisar a conversa {ConversationId}.", input.ConversationId);
                return new AnalysisOutcome(AnalysisResultKind.Failed, null, ex.Message);
            }

            cost += await budget.SettleAsync(reservation.Id, completion.Model, completion.InputTokens, completion.OutputTokens, ct);

            var parsed = AiAnalysisSchema.TryParse(completion.Text, outcomes, input.AllowOpen);
            if (parsed is null)
            {
                lastError = "A resposta da IA não respeitou o schema.";
                logger.LogWarning("Resposta fora do schema na conversa {ConversationId}, tentativa {Attempt}.",
                    input.ConversationId, attempt);
                continue;
            }

            var analysis = Materialize(existing, input, parsed, completion.Model, cost);
            await db.SaveChangesAsync(ct);

            return new AnalysisOutcome(AnalysisResultKind.Analyzed, analysis, null);
        }

        return new AnalysisOutcome(AnalysisResultKind.Failed, null, lastError);
    }

    // Falha também precisa fechar a reserva, senão ela segura a estimativa até a
    // janela virar. Ordem: custo real informado pelo provedor > devolver o
    // dinheiro (nada foi gerado) > manter o débito (pode ter sido cobrado).
    private async Task CloseReservationAsync(Guid reservationId, AiProviderException ex, CancellationToken ct)
    {
        if (ex.Usage is { } usage)
            await budget.SettleAsync(reservationId, usage.Model, usage.InputTokens, usage.OutputTokens, ct);
        else if (!ex.MayHaveBeenCharged)
            await budget.ReleaseAsync(reservationId, ct);
    }

    private ConversationAiAnalysis Materialize(
        ConversationAiAnalysis? existing,
        ConversationAnalysisInput input,
        ConversationAnalysisResult parsed,
        string model,
        decimal cost)
    {
        // A leitura anterior deixa de ser a corrente, mas continua no banco: é o
        // histórico que permite comparar o que a IA achava antes.
        if (existing is not null)
            existing.IsCurrent = false;

        var analysis = new ConversationAiAnalysis
        {
            Id = Guid.NewGuid(),
            ConversationId = input.ConversationId,
            IsCurrent = true,
        };

        analysis.MessageCount = input.MessageCount;
        analysis.LastMessageAt = input.LastMessageAt;
        analysis.StatusCode = parsed.Status;
        analysis.StatusConfidence = parsed.Confidence;
        analysis.StatusEvidence = Trim(parsed.Evidence, 500);
        analysis.LossReason = parsed.LossReason;
        analysis.AskedForSale = parsed.AskedForSale;
        analysis.IgnoredBuyingSignal = parsed.IgnoredBuyingSignal;
        analysis.Objections = parsed.Objections.Count == 0 ? null : Trim(string.Join("; ", parsed.Objections), 500);
        analysis.ShouldRecontact = parsed.ShouldRecontact;
        analysis.RecontactReason = Trim(parsed.RecontactReason, 300);
        analysis.SuggestedMessage = Trim(parsed.SuggestedMessage, 500);
        analysis.Interest = Trim(parsed.Interest, 200);
        analysis.Summary = Trim(parsed.Summary, 500);
        analysis.ConductAlert = Trim(parsed.ConductAlert, 300);
        analysis.Model = model;
        analysis.CostBrl = cost;
        analysis.AnalyzedAt = DateTime.UtcNow;

        db.Add(analysis);

        return analysis;
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
