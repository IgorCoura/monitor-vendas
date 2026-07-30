using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Data;

namespace MonitorVendas.Api.Features.Outcomes;

// Versão do catálogo: muda a cada alteração de tipo/termo e invalida o cache do
// matcher (consultado a cada evento de etiqueta).
public sealed class OutcomeCatalogVersion
{
    private int _version;

    public int Current => Volatile.Read(ref _version);

    public void Bump() => Interlocked.Increment(ref _version);
}

public sealed class OutcomeLabelMatcher(OutcomeCatalogVersion version)
{
    private int _cachedVersion = -1;
    private Dictionary<string, string> _byKey = [];

    // Devolve o código do tipo de desfecho da etiqueta, ou null se ela não
    // representa nenhum. Comparação por igualdade da chave normalizada — "venda"
    // não casa "vendas" nem "venda cancelada" (cada variação é cadastrada).
    public async Task<string?> MatchAsync(AppDbContext db, string? labelName, CancellationToken ct)
    {
        var key = LabelNormalizer.Normalize(labelName);
        if (key.Length == 0)
            return null;

        await EnsureLoadedAsync(db, ct);
        return _byKey.GetValueOrDefault(key);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetMapAsync(AppDbContext db, CancellationToken ct)
    {
        await EnsureLoadedAsync(db, ct);
        return _byKey;
    }

    private async Task EnsureLoadedAsync(AppDbContext db, CancellationToken ct)
    {
        var current = version.Current;
        if (_cachedVersion == current)
            return;

        var terms = await db.Set<OutcomeLabelTerm>().AsNoTracking()
            .Join(db.Set<ConversationOutcomeType>().AsNoTracking().Where(t => t.Active),
                term => term.OutcomeTypeCode,
                type => type.Code,
                (term, type) => new { term.NormalizedKey, type.Code })
            .ToListAsync(ct);

        _byKey = terms
            .GroupBy(t => t.NormalizedKey)
            .ToDictionary(g => g.Key, g => g.First().Code, StringComparer.Ordinal);
        _cachedVersion = current;
    }
}
