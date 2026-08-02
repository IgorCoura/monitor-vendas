using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;

namespace MonitorVendas.Tests.Ai;

// O corte de conversa longa e o mascaramento decidem o que a IA vê. Errar aqui
// significa mandar dado pessoal para fora ou cegar a análise justamente no
// trecho onde mora o desfecho.
public class TranscriptTrimTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly DateTime Start = new(2026, 7, 1, 13, 0, 0, DateTimeKind.Utc);

    private static TranscriptMessage Line(int index, string text) =>
        new(index % 2 == 0 ? MessageDirection.Inbound : MessageDirection.Outbound,
            Start.AddMinutes(index), text, "conversation");

    // Conversa longa é cortada no MEIO: o começo dá contexto e o fim tem o
    // desfecho. Tirar o fim seria cegar a análise no que mais importa.
    [Fact]
    public void Build_WhenTooLong_KeepsTheStartAndTheEnd()
    {
        var messages = Enumerable.Range(0, 80).Select(i => Line(i, $"mensagem número {i} com bastante texto para ocupar espaço")).ToList();

        var transcript = TranscriptBuilder.Build(messages, null, null, SaoPaulo, true, 0, maxChars: 1_200);

        Assert.Contains("mensagem número 0 ", transcript);
        Assert.Contains("mensagem número 79 ", transcript);
        Assert.Contains("mensagens omitidas", transcript);
        Assert.True(transcript.Length <= 1_200, $"transcrição ficou com {transcript.Length} caracteres");
    }

    // Conversa curta não é cortada e não ganha marca de omissão.
    [Fact]
    public void Build_WhenItFits_KeepsEverything()
    {
        var messages = Enumerable.Range(0, 5).Select(i => Line(i, $"linha {i}")).ToList();

        var transcript = TranscriptBuilder.Build(messages, null, null, SaoPaulo, true, 0);

        Assert.DoesNotContain("omitidas", transcript);
        Assert.All(Enumerable.Range(0, 5), i => Assert.Contains($"linha {i}", transcript));
    }

    // Poucas mensagens muito longas não são cortadas pelo meio: com menos de 4
    // linhas não há "meio" para omitir.
    [Fact]
    public void Build_WithVeryFewLines_DoesNotTrim()
    {
        var messages = new List<TranscriptMessage> { Line(0, new string('x', 600)), Line(1, new string('y', 600)) };

        var transcript = TranscriptBuilder.Build(messages, null, null, SaoPaulo, true, 0, maxChars: 200);

        Assert.DoesNotContain("omitidas", transcript);
    }

    // Quem começou muda o cabeçalho: conversa iniciada pelo vendedor é disparo,
    // e o modelo precisa saber disso para julgar o atendimento.
    [Fact]
    public void Build_AnnouncesWhoStarted()
    {
        Assert.Contains("iniciada pelo cliente",
            TranscriptBuilder.Build([Line(0, "oi")], null, null, SaoPaulo, true, 0));
        Assert.Contains("iniciada pelo vendedor",
            TranscriptBuilder.Build([Line(0, "oi")], null, null, SaoPaulo, false, 0));
    }

    // Nome curto demais não é mascarado: trocar duas letras dentro de palavras
    // comuns destruiria a transcrição inteira.
    [Fact]
    public void Mask_IgnoresVeryShortNames()
    {
        Assert.Equal("Ana vai pagar", TranscriptBuilder.Mask("Ana vai pagar", "An", null));
        Assert.Equal("[CLIENTE] vai pagar", TranscriptBuilder.Mask("Ana vai pagar", "Ana", null));
    }

    // O telefone sai em qualquer formatação, e o número curto do cliente também
    // (o WhatsApp mostra os 8 finais em alguns eventos).
    [Fact]
    public void Mask_RemovesPhoneInEveryShape()
    {
        var masked = TranscriptBuilder.Mask(
            "me chama no 5511988887777 ou (11) 98888-7777", null, "5511988887777");

        Assert.DoesNotContain("5511988887777", masked);
        Assert.DoesNotContain("98888-7777", masked);
    }

    // Valor com ponto e vírgula não é telefone: mascarar preço apagaria o dado
    // mais importante da negociação.
    [Fact]
    public void Mask_KeepsMoneyAndShortNumbers()
    {
        Assert.Equal("fica 1.200,00 à vista", TranscriptBuilder.Mask("fica 1.200,00 à vista", null, null));
        Assert.Equal("são 12 unidades", TranscriptBuilder.Mask("são 12 unidades", null, null));
    }

    // Telefone curto demais para ser telefone não vira máscara.
    [Fact]
    public void Mask_WithoutEnoughDigits_DoesNothing()
    {
        Assert.Equal("liga 1234", TranscriptBuilder.Mask("liga 1234", null, "1234"));
    }

    // Cada tipo de mídia tem seu rótulo; o desconhecido vira "[mídia]" em vez de
    // linha vazia.
    [Fact]
    public void MediaLabel_CoversEveryKnownType()
    {
        Assert.Equal("[imagem]", TranscriptBuilder.MediaLabel("imageMessage"));
        Assert.Equal("[documento]", TranscriptBuilder.MediaLabel("documentMessage"));
        Assert.Equal("[figurinha]", TranscriptBuilder.MediaLabel("stickerMessage"));
        Assert.Equal("[localização]", TranscriptBuilder.MediaLabel("locationMessage"));
        Assert.Equal("[contato]", TranscriptBuilder.MediaLabel("contactMessage"));
        Assert.Equal("[vídeo de 30s]", TranscriptBuilder.MediaLabel("videoMessage", 30));
        Assert.Equal("[mídia]", TranscriptBuilder.MediaLabel("tipoNovoDoWhatsapp"));
    }

    // O silêncio sai com vírgula decimal: número em ponto no meio de um texto em
    // pt-BR é convite a leitura errada.
    [Fact]
    public void Build_WritesSilenceInBrazilianDecimal()
    {
        Assert.Contains("2,5 horas úteis", TranscriptBuilder.Build([Line(0, "oi")], null, null, SaoPaulo, true, 2.5));
        Assert.Contains("0 horas úteis", TranscriptBuilder.Build([Line(0, "oi")], null, null, SaoPaulo, true, 0));
    }
}
