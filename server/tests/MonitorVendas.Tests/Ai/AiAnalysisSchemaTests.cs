using System.Text.Json;
using MonitorVendas.Api.Features.Ai.Analysis;

namespace MonitorVendas.Tests.Ai;

// O schema é a trava contra injeção de prompt: status inventado não passa. E o
// parser precisa aguentar resposta malformada sem derrubar a análise.
public class AiAnalysisSchemaTests
{
    private static readonly IReadOnlyList<OutcomeChoice> Catalog =
    [
        new("sale", "Vendas"),
        new("lost", "Clientes perdidos"),
    ];

    private static string Answer(object body) => JsonSerializer.Serialize(body);

    // O catálogo do usuário vira o enum fechado do schema; `open` só entra
    // quando a conversa ainda pode estar viva.
    [Fact]
    public void BuildSchema_ClosesTheStatusEnumOnTheCatalog()
    {
        var comOpen = BuildSchemaDoc(allowOpen: true);
        var semOpen = BuildSchemaDoc(allowOpen: false);

        Assert.Equal(["open", "sale", "lost"], StatusEnum(comOpen));
        Assert.Equal(["sale", "lost"], StatusEnum(semOpen));
    }

    // A taxonomia de perda também é fechada — motivo livre não vira gráfico.
    [Fact]
    public void BuildSchema_ClosesTheLossReasonEnum()
    {
        var schema = BuildSchemaDoc(allowOpen: true);
        var reasons = schema.RootElement.GetProperty("properties").GetProperty("lossReason")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(AiAnalysisSchema.LossReasons, reasons);
    }

    // O prompt leva o catálogo com nome ao lado: "aguardando-pagamento" sozinho
    // não diz nada ao modelo.
    [Fact]
    public void BuildUserPrompt_ExplainsEachStatusAndDelimitsTheTranscript()
    {
        var prompt = AiAnalysisSchema.BuildUserPrompt(Catalog, allowOpen: true, "Cliente: oi");

        Assert.Contains("sale: Vendas.", prompt);
        Assert.Contains("lost: Clientes perdidos.", prompt);
        Assert.Contains("open:", prompt);
        Assert.Contains("<<<TRANSCRICAO", prompt);
        Assert.Contains("Cliente: oi", prompt);
    }

    // Conversa parada perde o `open` do próprio prompt — onde o relógio decide,
    // ele decide antes da IA.
    [Fact]
    public void BuildUserPrompt_WithoutOpen_DoesNotOfferIt()
    {
        var prompt = AiAnalysisSchema.BuildUserPrompt(Catalog, allowOpen: false, "Cliente: oi");

        Assert.DoesNotContain("open:", prompt);
    }

    // Um áudio e vários áudios têm frases diferentes: o singular evita "1 áudios".
    [Fact]
    public void BuildUserPrompt_AnnouncesAttachedAudioInSingularAndPlural()
    {
        Assert.Contains("1 áudio anexado a esta mensagem",
            AiAnalysisSchema.BuildUserPrompt(Catalog, true, "t", audioCount: 1));
        Assert.Contains("3 áudios anexados a esta mensagem",
            AiAnalysisSchema.BuildUserPrompt(Catalog, true, "t", audioCount: 3));
        Assert.DoesNotContain("anexado a esta mensagem",
            AiAnalysisSchema.BuildUserPrompt(Catalog, true, "t"));
    }

    // Caminho feliz do parser, com os campos opcionais preenchidos.
    [Fact]
    public void TryParse_ReadsAFullAnswer()
    {
        var result = AiAnalysisSchema.TryParse(Answer(new
        {
            status = "lost",
            confidence = 0.8,
            evidence = "achei caro",
            lossReason = "preco",
            askedForSale = true,
            ignoredBuyingSignal = true,
            objections = new[] { "preço", "prazo" },
            shouldRecontact = true,
            recontactReason = "sumiu",
            suggestedMessage = "posso melhorar",
            interest = "kit",
            summary = "resumo",
            conductAlert = "grosseria",
        }), Catalog, allowOpen: true);

        Assert.NotNull(result);
        Assert.Equal("lost", result!.Status);
        Assert.Equal(0.8, result.Confidence);
        Assert.Equal(["preço", "prazo"], result.Objections);
        Assert.Equal("grosseria", result.ConductAlert);
    }

    // É exatamente essa a cara de uma injeção de prompt bem-sucedida: status
    // fora do catálogo. O parser recusa em vez de gravar.
    [Fact]
    public void TryParse_RejectsStatusOutsideTheCatalog()
    {
        Assert.Null(AiAnalysisSchema.TryParse(
            Answer(new { status = "inventado", confidence = 0.9, summary = "x" }), Catalog, allowOpen: true));

        // `open` só vale quando a conversa pode estar viva.
        Assert.Null(AiAnalysisSchema.TryParse(
            Answer(new { status = "open", confidence = 0.9, summary = "x" }), Catalog, allowOpen: false));
    }

    // Resposta truncada ou fora do formato devolve nulo — quem chama decide se
    // tenta de novo, ninguém explode.
    [Fact]
    public void TryParse_ReturnsNullForMalformedAnswers()
    {
        Assert.Null(AiAnalysisSchema.TryParse("{ isso não é json", Catalog, true));
        Assert.Null(AiAnalysisSchema.TryParse("[]", Catalog, true));
        Assert.Null(AiAnalysisSchema.TryParse(Answer(new { confidence = 0.5 }), Catalog, true));
    }

    // Motivo de perda fora da taxonomia vira "outro" em vez de sumir: a
    // informação de que houve motivo continua valendo.
    [Fact]
    public void TryParse_NormalizesUnknownLossReason()
    {
        var result = AiAnalysisSchema.TryParse(
            Answer(new { status = "lost", confidence = 0.5, lossReason = "chovia", summary = "x" }), Catalog, true);

        Assert.Equal("outro", result!.LossReason);
    }

    // Confiança fora de 0–1 é grampeada: 1.8 de confiança não existe.
    [Fact]
    public void TryParse_ClampsConfidence()
    {
        Assert.Equal(1, AiAnalysisSchema.TryParse(
            Answer(new { status = "sale", confidence = 1.8, summary = "x" }), Catalog, true)!.Confidence);
        Assert.Equal(0, AiAnalysisSchema.TryParse(
            Answer(new { status = "sale", confidence = -3, summary = "x" }), Catalog, true)!.Confidence);
        // Campo ausente ou de outro tipo vale zero, não quebra.
        Assert.Equal(0, AiAnalysisSchema.TryParse(
            Answer(new { status = "sale", summary = "x" }), Catalog, true)!.Confidence);
    }

    // Texto vazio é ausência: gravar "" faria a tela mostrar campo em branco
    // como se o modelo tivesse respondido algo.
    [Fact]
    public void TryParse_TreatsEmptyStringsAsAbsent()
    {
        var result = AiAnalysisSchema.TryParse(Answer(new
        {
            status = "sale",
            confidence = 0.5,
            evidence = "",
            objections = new[] { "", "válida" },
            summary = "x",
        }), Catalog, true);

        Assert.Null(result!.Evidence);
        Assert.Equal(["válida"], result.Objections);
    }

    // O rótulo de cada motivo mora ao lado da taxonomia; código desconhecido sai
    // como veio, sem inventar tradução.
    [Fact]
    public void FriendlyLossReason_TranslatesEveryKnownCode()
    {
        Assert.All(AiAnalysisSchema.LossReasons, code =>
            Assert.False(string.IsNullOrWhiteSpace(AiAnalysisSchema.FriendlyLossReason(code))));

        Assert.Equal("Preço", AiAnalysisSchema.FriendlyLossReason("preco"));
        Assert.Equal("codigo-novo", AiAnalysisSchema.FriendlyLossReason("codigo-novo"));
        Assert.Null(AiAnalysisSchema.FriendlyLossReason(null));
    }

    private static JsonDocument BuildSchemaDoc(bool allowOpen) =>
        JsonDocument.Parse(AiAnalysisSchema.BuildSchema(Catalog, allowOpen));

    private static List<string?> StatusEnum(JsonDocument schema) =>
        [.. schema.RootElement.GetProperty("properties").GetProperty("status")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString())];
}
