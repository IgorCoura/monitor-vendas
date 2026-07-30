using MonitorVendas.Api.Common;

namespace MonitorVendas.Api.Features.Contacts;

// Uma linha por contato. As colunas singulares (vendedor, número, banimento) vêm da
// conversa com a mensagem mais recente DENTRO do período; as datas são o mín/máx do
// período; o desfecho é o mais recente entre as conversas do contato.
public record ContactRowDto(
    Guid ContactId,
    string Name,
    string Phone,
    DateTime FirstMessageAt,
    DateTime LastMessageAt,
    string? OutcomeTypeCode,
    string? Outcome,
    IReadOnlyList<string> Labels,
    Guid? SellerId,
    string? SellerName,
    string? SellerNumber,
    string NumberStatus,
    bool NumberBanned);

public record ContactPageDto(IReadOnlyList<ContactRowDto> Items, int Page, int PageSize, int Total);

// `OutcomeTypes` vazio = todos. O código especial `none` seleciona quem está sem
// desfecho — sem ele não daria para exportar "contatos que ninguém fechou".
public record ContactFilter(
    DateTime? FromUtc,
    DateTime? ToUtc,
    Guid? SellerId,
    IReadOnlyList<string> OutcomeTypes,
    bool? Banned)
{
    public const string NoOutcome = "none";

    // Prévia, exportação e envio por WhatsApp leem os mesmos parâmetros — o
    // parsing mora aqui para não divergir entre os três endpoints.
    public static ContactFilter? TryCreate(DateTime? from, DateTime? to, Guid? sellerId, string? outcomeTypes, bool? banned)
    {
        var fromUtc = UtcDates.ToUtc(from);
        var toUtc = UtcDates.ToUtc(to);
        if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
            return null;

        var types = (outcomeTypes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ContactFilter(fromUtc, toUtc, sellerId, types, banned);
    }
}
