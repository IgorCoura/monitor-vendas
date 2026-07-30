namespace MonitorVendas.Api.Integrations.Ai;

// O pedido é descrito de forma neutra: cada provider traduz o schema para o
// dialeto dele (responseSchema no Gemini, json_schema na OpenAI). Trocar de LLM
// é escrever um IAiProvider novo — nada fora deste namespace conhece o Gemini.
// Mídia enviada junto do texto. `Seconds` só serve para estimar o custo antes de
// enviar — o valor real vem medido na resposta.
public sealed record AiAttachment(string MimeType, string Base64Data, double? Seconds = null);

public sealed record AiRequest(
    string SystemPrompt,
    string UserPrompt,
    string? ResponseJsonSchema = null,
    int? MaxOutputTokens = null,
    IReadOnlyList<AiAttachment>? Attachments = null);

// `InputAudioTokens` é um subconjunto de `InputTokens`: o provedor cobra áudio a
// uma tarifa própria, e somar tudo como texto subfaturaria o saldo em silêncio.
public sealed record AiCompletion(
    string Text,
    string Model,
    int InputTokens,
    int OutputTokens,
    int InputAudioTokens = 0);

public interface IAiProvider
{
    string Name { get; }

    Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken ct = default);
}

public sealed record AiUsageTokens(string Model, int InputTokens, int OutputTokens, int InputAudioTokens = 0);

// `MayHaveBeenCharged` decide o destino da reserva de saldo: erro que impediu a
// geração (4xx, conexão recusada) devolve o dinheiro; timeout depois do envio
// mantém o débito, porque o provedor provavelmente gerou e cobrou.
//
// `Usage` é o caso melhor: a chamada falhou mas o provedor informou o que
// consumiu (resposta truncada, por exemplo). Aí a reserva vira débito pelo custo
// real, em vez de ficar segurando a estimativa até a janela virar.
public sealed class AiProviderException(string message, bool mayHaveBeenCharged, Exception? inner = null)
    : Exception(message, inner)
{
    public bool MayHaveBeenCharged { get; } = mayHaveBeenCharged;

    public AiUsageTokens? Usage { get; init; }
}
