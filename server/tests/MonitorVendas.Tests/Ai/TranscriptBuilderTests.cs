using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;

namespace MonitorVendas.Tests.Ai;

public class TranscriptBuilderTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly DateTime Start = new(2026, 7, 1, 13, 0, 0, DateTimeKind.Utc);

    private static TranscriptMessage In(string? text, int minutes = 0, string type = "conversation") =>
        new(MessageDirection.Inbound, Start.AddMinutes(minutes), text, type);

    private static TranscriptMessage Out(string? text, int minutes = 0, string type = "conversation") =>
        new(MessageDirection.Outbound, Start.AddMinutes(minutes), text, type);

    private static string Build(params TranscriptMessage[] messages) =>
        TranscriptBuilder.Build(messages, "Maria Silva", "5511988887777", SaoPaulo, true, 2.5);

    // Cada lado é identificado e o horário sai convertido para o fuso do relatório.
    [Fact]
    public void Build_LabelsBothSidesInLocalTime()
    {
        var transcript = Build(In("bom dia"), Out("bom dia, tudo bem?", 10));

        Assert.Contains("Cliente (01/07 10:00): bom dia", transcript);
        Assert.Contains("Vendedor (01/07 10:10): bom dia, tudo bem?", transcript);
    }

    // O silêncio em horas úteis vai no cabeçalho: sem ele a IA chuta se a conversa
    // ainda está viva.
    [Fact]
    public void Build_AnnouncesSilenceInBusinessHours()
    {
        Assert.Contains("Silêncio desde a última mensagem: 2,5 horas úteis.", Build(In("oi")));
    }

    // Nome e telefone do cliente não saem daqui: viram marcadores antes do envio.
    [Fact]
    public void Build_MasksContactNameAndPhone()
    {
        var transcript = Build(In("aqui é a Maria Silva, meu zap é 5511988887777"));

        Assert.DoesNotContain("Maria Silva", transcript);
        Assert.DoesNotContain("5511988887777", transcript);
        Assert.Contains("[CLIENTE]", transcript);
        Assert.Contains("[TELEFONE]", transcript);
    }

    // Telefone de terceiro escrito no meio da conversa também sai — o cliente
    // costuma mandar o contato de outra pessoa.
    [Fact]
    public void Mask_RemovesAnyPhoneLikeNumber()
    {
        Assert.Equal("fala com o [TELEFONE]", TranscriptBuilder.Mask("fala com o (11) 97777-6666", null, null));
    }

    // Valor não é telefone: mascarar preço cegaria a análise de objeção.
    [Fact]
    public void Mask_KeepsPrices()
    {
        Assert.Equal("fica 1.200,00 à vista", TranscriptBuilder.Mask("fica 1.200,00 à vista", null, null));
    }

    // Mensagem sem texto (áudio, imagem) entra como rótulo, não como linha vazia.
    [Fact]
    public void Build_LabelsMediaMessages()
    {
        var transcript = Build(In(null, 0, "audioMessage"), Out(null, 5, "imageMessage"));

        Assert.Contains("[áudio]", transcript);
        Assert.Contains("[imagem]", transcript);
    }

    // A duração entra no rótulo do áudio: sem ela, um recado de 3 segundos e um
    // desabafo de 4 minutos chegam à IA como o mesmo evento.
    [Fact]
    public void Build_LabelsAudioWithItsDuration()
    {
        var transcript = TranscriptBuilder.Build(
            [new TranscriptMessage(MessageDirection.Inbound, Start, null, "audioMessage", 45)],
            null, null, SaoPaulo, true, 0);

        Assert.Contains("[áudio de 45s]", transcript);
    }

    // Regressão (31/07/2026): áudio enviado ao modelo ia como blob solto e a
    // transcrição dizia só "[áudio de 45s]" — sem saber a qual trecho cada anexo
    // pertencia, o modelo tratava tudo como conteúdo não textual e ignorava. O
    // número liga o marcador à ordem dos anexos na chamada.
    [Fact]
    public void Build_NumbersTheAudiosThatWereAttached()
    {
        var transcript = TranscriptBuilder.Build(
            [
                new TranscriptMessage(MessageDirection.Inbound, Start, null, "audioMessage", 45, AudioIndex: 1),
                new TranscriptMessage(MessageDirection.Inbound, Start.AddMinutes(5), null, "pttMessage", 12, AudioIndex: 2),
            ],
            null, null, SaoPaulo, true, 0);

        Assert.Contains("[áudio 1 de 45s]", transcript);
        Assert.Contains("[áudio 2 de 12s]", transcript);
    }

    // Áudio que não pôde ser baixado continua sem número: o modelo não recebeu
    // aquele trecho e não deve procurar por um anexo que não existe.
    [Fact]
    public void Build_LeavesUnattachedAudioWithoutNumber()
    {
        var transcript = TranscriptBuilder.Build(
            [new TranscriptMessage(MessageDirection.Inbound, Start, null, "audioMessage", 45)],
            null, null, SaoPaulo, true, 0);

        Assert.Contains("[áudio de 45s]", transcript);
        Assert.DoesNotContain("[áudio 1", transcript);
    }

    // Sem duração conhecida (mensagem antiga, payload sem o campo) o rótulo
    // continua funcionando — nunca vira "[áudio de s]".
    [Fact]
    public void MediaLabel_WithoutDuration_StaysPlain()
    {
        Assert.Equal("[áudio]", TranscriptBuilder.MediaLabel("audioMessage"));
        Assert.Equal("[áudio]", TranscriptBuilder.MediaLabel("audioMessage", 0));
        Assert.Equal("[áudio 3]", TranscriptBuilder.MediaLabel("audioMessage", null, 3));
        Assert.Equal("[imagem]", TranscriptBuilder.MediaLabel("imageMessage", 45));
    }

    // Conversa comprida é cortada no meio: o fim é onde mora o desfecho.
    [Fact]
    public void Build_WhenTooLong_KeepsTheEnding()
    {
        var messages = Enumerable.Range(0, 200)
            .Select(i => In(new string('x', 200) + i, i))
            .Append(In("fechado, pode mandar o link", 300))
            .ToArray();

        var transcript = TranscriptBuilder.Build(messages, null, null, SaoPaulo, true, 0, maxChars: 3_000);

        Assert.Contains("mensagens omitidas", transcript);
        Assert.Contains("fechado, pode mandar o link", transcript);
        Assert.True(transcript.Length <= 3_000);
    }
}
