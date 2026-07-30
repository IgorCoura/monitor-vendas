using System.Security.Cryptography;
using System.Text;

namespace MonitorVendas.Api.Features.Ai.Analysis;

// Síntese por vendedor guardada. A chave é o **conjunto de análises** que a
// alimentou, não o período: o painel manda `to = agora`, que muda a cada minuto,
// e um cache chaveado por período nunca acertaria. Mesmas conversas com as mesmas
// leituras ⇒ mesma síntese, de graça.
public class SellerAiSynthesis
{
    public Guid Id { get; set; }
    public Guid SellerId { get; set; }
    public string SellerName { get; set; } = string.Empty;
    public string InputsHash { get; set; } = string.Empty;

    public string? Overview { get; set; }
    public string? Strengths { get; set; }
    public string? Improvements { get; set; }
    public string? DominantLossPattern { get; set; }
    public string? TrainingSuggestion { get; set; }

    public string Model { get; set; } = string.Empty;
    public decimal CostBrl { get; set; }
    public int ConversationsCount { get; set; }
    public DateTime CreatedAt { get; set; }

    private const char Separator = '\n';

    public static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(Separator, StringSplitOptions.RemoveEmptyEntries);

    public static string? Join(IReadOnlyList<string> items) =>
        items.Count == 0 ? null : string.Join(Separator, items);

    // Só os ids das análises entram no hash. Como cada reanálise cria uma linha
    // nova (o histórico), um id novo já significa leitura nova — não é preciso
    // olhar a data. E data aqui seria armadilha: o timestamptz do Postgres trunca
    // em microssegundos, então o valor em memória nunca casaria com o relido.
    public static string HashOf(IEnumerable<Guid> analysisIds)
    {
        var payload = string.Join('|', analysisIds.OrderBy(id => id).Select(id => id.ToString("N")));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
