using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Integrations.Ai;

namespace MonitorVendas.Api.Features.Ai;

public sealed record AiEstimate(
    int Conversations,
    // Quantas já têm leitura que ainda serve. Com `Force` são refeitas assim
    // mesmo (e custam); em "só o que mudou" são justamente as que saem da conta.
    int Cached,
    int Sellers,
    decimal EstimatedBrl,
    decimal Available,
    bool Affordable,
    bool BudgetEnabled,
    bool Truncated);

// A conta de quanto uma rodada vai custar, antes de gastar. Uma implementação
// só: a tela mostra, o endpoint recusa e o runner confere na hora de rodar —
// estimativa que diverge do que é cobrado seria pior que não ter estimativa.
public sealed class AiJobEstimator(
    AppDbContext db,
    ConversationAiWorkset workset,
    AiBudget budget,
    AiCostCalculator calculator,
    IOptions<AiOptions> aiOptions,
    IOptions<AiJobOptions> jobOptions)
{
    // Tamanho típico do prompt de uma síntese: métricas + uma linha por conversa.
    private const int SynthesisPromptChars = 4_000;

    // A mesma frase no endpoint que recusa e no job que desiste: o usuário
    // precisa ler a mesma coisa venha o bloqueio de onde vier.
    public static string NoBudgetMessage(AiJobKind kind) => kind == AiJobKind.Analyze
        ? "Análise não realizada por falta de saldo."
        : "Síntese não realizada por falta de saldo.";

    public async Task<AiEstimate> EstimateAsync(AiJobKind kind, AiJobFilters filters, CancellationToken ct = default)
    {
        var (conversations, truncated) = await workset.LoadAsync(
            new ConversationAiFilter(
                filters.From,
                filters.To,
                filters.SellerIds,
                jobOptions.Value.MaxConversationsPerRun,
                Force: kind == AiJobKind.Analyze && filters.Force,
                filters.IncludeAudio),
            ct);

        // Seleção explícita da tela ganha do período, igual ao runner.
        if (filters.ConversationIds.Count > 0)
        {
            var wanted = filters.ConversationIds.ToHashSet();
            conversations = [.. conversations.Where(c => wanted.Contains(c.ConversationId))];
        }

        var ids = conversations.Select(c => c.ConversationId).ToList();
        var current = await db.Set<ConversationAiAnalysis>().AsNoTracking()
            .Where(a => a.IsCurrent && ids.Contains(a.ConversationId))
            .ToDictionaryAsync(a => a.ConversationId, ct);

        // "Ainda serve" é decidido pelo mesmo `StillServes` do analisador: o que
        // ele reaproveitaria é exatamente o que aqui não é cobrado.
        var reusable = conversations
            .Where(c => current.TryGetValue(c.ConversationId, out var analysis) && analysis.StillServes(c.Input))
            .Select(c => c.ConversationId)
            .ToHashSet();

        var settings = aiOptions.Value;
        var status = await budget.GetStatusAsync(ct);
        var estimate = 0m;
        var sellers = 0;

        if (kind == AiJobKind.Analyze)
        {
            // Com `Force` toda conversa do filtro custa — o botão existe para reler
            // ignorando o que já está lá. Sem ele, só o que mudou entra na conta.
            foreach (var conversation in conversations)
            {
                if (!filters.Force && reusable.Contains(conversation.ConversationId))
                    continue;

                var prompt = AiAnalysisSchema.SystemPrompt + conversation.Input.Transcript;
                estimate += calculator.EstimateBrl(
                    settings.Model, prompt, settings.MaxOutputTokens, budget.MarginPercent, conversation.AudioSeconds);
            }
        }
        else
        {
            // A síntese roda sobre as leituras correntes: vendedor sem nenhuma não
            // gera chamada. E o cache dela é chaveado pelo conjunto de leituras,
            // então quem não mudou volta de graça — mesma conta do runner.
            var bySeller = conversations
                .Where(c => c.SellerId is not null && current.ContainsKey(c.ConversationId))
                .GroupBy(c => c.SellerId!.Value)
                .ToDictionary(g => g.Key, g => SellerAiSynthesis.HashOf(g.Select(c => current[c.ConversationId].Id)));

            var hashes = bySeller.Values.ToList();
            var done = await db.Set<SellerAiSynthesis>().AsNoTracking()
                .Where(s => hashes.Contains(s.InputsHash))
                .Select(s => new { s.SellerId, s.InputsHash })
                .ToListAsync(ct);

            sellers = bySeller
                .Count(pair => filters.Force ||
                               !done.Any(s => s.SellerId == pair.Key && s.InputsHash == pair.Value));

            estimate = sellers * calculator.EstimateBrl(
                settings.Model, new string('x', SynthesisPromptChars), settings.MaxOutputTokens, budget.MarginPercent);
        }

        return new AiEstimate(
            conversations.Count,
            reusable.Count,
            sellers,
            estimate,
            status.Available,
            !status.Enabled || estimate <= status.Available,
            status.Enabled,
            truncated);
    }
}
