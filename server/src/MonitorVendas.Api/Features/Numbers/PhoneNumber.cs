namespace MonitorVendas.Api.Features.Numbers;

// Comparar telefone por igualdade de string não funciona: o mesmo aparelho
// aparece como "11968608425" no cadastro antigo e "5511968608425" no JID do
// WhatsApp, e números antigos ainda circulam sem o 9º dígito. Tudo aqui é
// pensado para o Brasil, que é a premissa do produto (DDI/DDD, fuso, expediente).
public static class PhoneNumber
{
    private const string BrazilCode = "55";

    // Só os dígitos: "+55 (11) 91234-4567" e "5511912344567" viram a mesma coisa.
    public static string DigitsOf(string? value) =>
        new([.. (value ?? string.Empty).Where(char.IsDigit)]);

    // "5511968608425@s.whatsapp.net" ou "5511968608425:12@s.whatsapp.net" → dígitos.
    public static string FromJid(string? jid)
    {
        if (string.IsNullOrWhiteSpace(jid))
            return string.Empty;

        var head = jid.AsSpan();
        var cut = head.IndexOfAny('@', ':');
        return DigitsOf(cut < 0 ? jid : head[..cut].ToString());
    }

    // Forma de armazenamento: sempre com DDI. Um cadastro de 10 ou 11 dígitos é
    // brasileiro sem o 55 — assumir isso é o que torna comparável o que já está
    // no banco.
    public static string ToStorage(string? value)
    {
        var digits = DigitsOf(value);

        return digits.Length is 10 or 11 ? BrazilCode + digits : digits;
    }

    // Chave de comparação: DDI fora e o 9º dígito de celular fora. É ela que diz
    // que "11968608425" e "5511968608425" são o mesmo número — e que impediria
    // colocar um WhatsApp legítimo em quarentena por diferença de formato.
    public static string ComparisonKey(string? value)
    {
        var digits = DigitsOf(value);

        if (digits.StartsWith(BrazilCode, StringComparison.Ordinal) && digits.Length is 12 or 13)
            digits = digits[BrazilCode.Length..];

        // Sobrou DDD + assinante. Celular novo tem 9 dígitos começando em 9; o
        // mesmo número em cadastro antigo tem 8.
        if (digits.Length == 11 && digits[2] == '9')
            digits = digits[..2] + digits[3..];

        return digits;
    }

    public static bool SameNumber(string? a, string? b)
    {
        var keyA = ComparisonKey(a);

        return keyA.Length > 0 && keyA == ComparisonKey(b);
    }

    // Exibição: +55 11 91234-4567. Só formata o que reconhece como brasileiro;
    // qualquer outra coisa sai como veio, para não inventar formato.
    public static string Format(string? value)
    {
        var digits = DigitsOf(value);
        if (digits.Length == 0)
            return string.Empty;

        var national = digits.StartsWith(BrazilCode, StringComparison.Ordinal) && digits.Length is 12 or 13
            ? digits[BrazilCode.Length..]
            : digits;

        if (national.Length is not (10 or 11))
            return digits;

        var area = national[..2];
        var subscriber = national[2..];
        var head = subscriber.Length == 9 ? subscriber[..5] : subscriber[..4];
        var tail = subscriber.Length == 9 ? subscriber[5..] : subscriber[4..];

        return $"+{BrazilCode} {area} {head}-{tail}";
    }
}
