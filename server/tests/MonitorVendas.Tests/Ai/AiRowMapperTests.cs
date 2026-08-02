using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Ai.Export;

namespace MonitorVendas.Tests.Ai;

// A regra da divergência mora só aqui: planilha, tela e síntese precisam
// concordar sobre o que é "a IA discorda da etiqueta".
public class AiRowMapperTests
{
    private static readonly Dictionary<string, string> TypeNames = new()
    {
        ["sale"] = "Vendas",
        ["lost"] = "Clientes perdidos",
    };

    private static ConversationContext Conversation(string? realOutcome) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Ana", "5511900001111", "Maria", "5511977776666",
            new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc),
            realOutcome,
            new ConversationAnalysisInput(Guid.NewGuid(), 4, DateTime.UtcNow, "transcrição", true));

    private static ConversationAiAnalysis Analysis(string statusCode) => new()
    {
        Id = Guid.NewGuid(),
        StatusCode = statusCode,
        StatusConfidence = 0.9,
        StatusEvidence = "achei caro",
        LossReason = "preco",
        AskedForSale = true,
        IgnoredBuyingSignal = false,
        Objections = "preço",
        ShouldRecontact = true,
        RecontactReason = "sumiu",
        SuggestedMessage = "posso melhorar",
        Interest = "kit",
        Summary = "resumo",
        AnalyzedAt = DateTime.UtcNow,
    };

    // Etiqueta e IA dizendo a mesma coisa: sem divergência, e o código vira nome.
    [Fact]
    public void ToRow_WhenBothAgree_IsNotDivergent()
    {
        var row = AiRowMapper.ToRow(Conversation("sale"), Analysis("sale"), TypeNames, null);

        Assert.False(row.Divergent);
        Assert.Equal("Vendas", row.RealOutcome);
        Assert.Equal("Vendas", row.AiStatus);
    }

    // Discordância real: é a linha que expõe etiquetagem esquecida.
    [Fact]
    public void ToRow_WhenTheyDisagree_IsDivergent()
    {
        var row = AiRowMapper.ToRow(Conversation("sale"), Analysis("lost"), TypeNames, null);

        Assert.True(row.Divergent);
        Assert.Equal("Vendas", row.RealOutcome);
        Assert.Equal("Clientes perdidos", row.AiStatus);
    }

    // "Em andamento" é ausência de desfecho: sem etiqueta, os dois concordam —
    // tratá-lo como valor faria toda conversa viva parecer divergente.
    [Fact]
    public void ToRow_OpenWithoutLabel_IsNotDivergent()
    {
        var row = AiRowMapper.ToRow(Conversation(null), Analysis(ConversationAiAnalysis.Open), TypeNames, null);

        Assert.False(row.Divergent);
        Assert.Null(row.RealOutcome);
        Assert.Equal("Em andamento", row.AiStatus);
    }

    // Mas conversa etiquetada que a IA acha viva DIVERGE: a etiqueta diz fechado
    // e o modelo diz que ainda está rolando.
    [Fact]
    public void ToRow_OpenWithLabel_IsDivergent()
    {
        var row = AiRowMapper.ToRow(Conversation("sale"), Analysis(ConversationAiAnalysis.Open), TypeNames, null);

        Assert.True(row.Divergent);
    }

    // Conversa sem análise não some da planilha: sai com o motivo, e nada da IA
    // é inventado na linha.
    [Fact]
    public void ToRow_WithoutAnalysis_CarriesTheReason()
    {
        var row = AiRowMapper.ToRow(Conversation("sale"), null, TypeNames, "Saldo de IA insuficiente.");

        Assert.Equal("Saldo de IA insuficiente.", row.NotAnalyzedReason);
        Assert.Null(row.AiStatus);
        Assert.Null(row.Confidence);
        Assert.False(row.Divergent);
        Assert.Equal("Vendas", row.RealOutcome);
    }

    // Código fora do catálogo (tipo removido depois da análise) aparece como
    // veio, em vez de sumir da linha.
    [Fact]
    public void ToRow_WithUnknownCode_FallsBackToTheCodeItself()
    {
        var row = AiRowMapper.ToRow(Conversation("removido"), Analysis("tambem-removido"), TypeNames, null);

        Assert.Equal("removido", row.RealOutcome);
        Assert.Equal("tambem-removido", row.AiStatus);
        Assert.True(row.Divergent);
    }

    // A comparação ignora caixa: "Sale" e "sale" são o mesmo desfecho.
    [Fact]
    public void ToRow_ComparesCodesIgnoringCase()
    {
        var row = AiRowMapper.ToRow(Conversation("SALE"), Analysis("sale"), TypeNames, null);

        Assert.False(row.Divergent);
    }
}
