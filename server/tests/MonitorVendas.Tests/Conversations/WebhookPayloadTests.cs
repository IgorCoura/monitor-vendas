using System.Text.Json;
using MonitorVendas.Api.Features.Conversations;

namespace MonitorVendas.Tests.Conversations;

// O parser do payload da Evolution é a porta de entrada de tudo: formato
// inesperado aqui vira métrica errada lá na frente, sem erro nenhum no meio.
public class WebhookPayloadTests
{
    private static JsonElement Data(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("data");

    // Texto simples, texto estendido e legenda de mídia são três formatos
    // diferentes para a mesma coisa: o que a pessoa escreveu.
    [Fact]
    public void ExtractText_ReadsTheThreeShapes()
    {
        Assert.Equal("oi", WebhookPayload.ExtractText(Data("""{"data":{"message":{"conversation":"oi"}}}""")));
        Assert.Equal("tudo bem?", WebhookPayload.ExtractText(
            Data("""{"data":{"message":{"extendedTextMessage":{"text":"tudo bem?"}}}}""")));
        Assert.Equal("olha a foto", WebhookPayload.ExtractText(
            Data("""{"data":{"message":{"imageMessage":{"caption":"olha a foto"}}}}""")));
    }

    // Mídia sem legenda não tem texto: devolver string vazia faria a conversa
    // parecer ter conteúdo escrito.
    [Fact]
    public void ExtractText_WithoutTextReturnsNull()
    {
        Assert.Null(WebhookPayload.ExtractText(Data("""{"data":{"message":{"audioMessage":{"seconds":10}}}}""")));
        Assert.Null(WebhookPayload.ExtractText(Data("""{"data":{}}""")));
    }

    // A duração vem como número ou como string, dependendo do tipo de mídia.
    [Fact]
    public void ExtractDurationSeconds_AcceptsNumberAndString()
    {
        Assert.Equal(45, WebhookPayload.ExtractDurationSeconds(
            Data("""{"data":{"message":{"audioMessage":{"seconds":45}}}}""")));
        Assert.Equal(12, WebhookPayload.ExtractDurationSeconds(
            Data("""{"data":{"message":{"pttMessage":{"seconds":"12"}}}}""")));
        Assert.Equal(30, WebhookPayload.ExtractDurationSeconds(
            Data("""{"data":{"message":{"videoMessage":{"seconds":30}}}}""")));
    }

    // Sem o campo (payload antigo) ou em mídia sem duração, fica nulo — a
    // transcrição diz "[áudio]" em vez de "[áudio de 0s]".
    [Fact]
    public void ExtractDurationSeconds_WithoutTheFieldIsNull()
    {
        Assert.Null(WebhookPayload.ExtractDurationSeconds(Data("""{"data":{"message":{"audioMessage":{}}}}""")));
        Assert.Null(WebhookPayload.ExtractDurationSeconds(Data("""{"data":{"message":{"imageMessage":{}}}}""")));
        Assert.Null(WebhookPayload.ExtractDurationSeconds(Data("""{"data":{}}""")));
    }

    // Grupo e broadcast ficam fora das métricas (decisão da V1).
    [Fact]
    public void IsGroupOrBroadcast_TellsThemApartFromDirectChats()
    {
        Assert.True(WebhookPayload.IsGroupOrBroadcast("12345-67890@g.us"));
        Assert.True(WebhookPayload.IsGroupOrBroadcast("status@broadcast"));
        Assert.False(WebhookPayload.IsGroupOrBroadcast("5511999998888@s.whatsapp.net"));
    }

    // O timestamp vem em segundos desde a época; ausente, quem chama usa a hora
    // de recebimento.
    [Fact]
    public void GetUnixTimestamp_ConvertsSecondsToUtc()
    {
        var data = Data("""{"data":{"messageTimestamp":1785000000}}""");

        var timestamp = WebhookPayload.GetUnixTimestamp(data, "messageTimestamp");

        Assert.Equal(new DateTime(2026, 7, 25, 17, 20, 0, DateTimeKind.Utc), timestamp);
        Assert.Null(WebhookPayload.GetUnixTimestamp(Data("""{"data":{}}"""), "messageTimestamp"));
    }

    // Campos de texto ausentes ou de outro tipo devolvem nulo em vez de
    // estourar: payload da Evolution muda de forma entre versões.
    [Fact]
    public void GetString_IsToleranteWithMissingOrWrongTypes()
    {
        var data = Data("""{"data":{"texto":"valor","numero":10}}""");

        Assert.Equal("valor", WebhookPayload.GetString(data, "texto"));
        Assert.Null(WebhookPayload.GetString(data, "numero"));
        Assert.Null(WebhookPayload.GetString(data, "inexistente"));
    }
}
