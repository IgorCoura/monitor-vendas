using MonitorVendas.Api.Features.Warmup;

namespace MonitorVendas.Tests.Warmup;

// O validador é a última barreira antes de a mensagem sair para o WhatsApp.
// Descartar e gerar de novo custa centavos; mandar um link custa o número.
public class WarmupContentValidatorTests
{
    private static GeneratedConversation Conversation(params (bool FromA, string Text)[] turns) =>
        new("combinar o almoço", [.. turns.Select(t => new GeneratedTurn(t.FromA, t.Text))]);

    // Conversa normal de colegas passa.
    [Fact]
    public void AcceptsOrdinaryColleagueChat()
    {
        var ok = WarmupContentValidator.IsAcceptable(
            Conversation((true, "bora almoçar?"), (false, "bora, 12h?"), (true, "fechou")), out var reason);

        Assert.True(ok);
        Assert.Null(reason);
    }

    // Link é o pior conteúdo possível num aquecimento: é o que transforma
    // conversa de colega em disparo publicitário aos olhos do WhatsApp.
    [Theory]
    [InlineData("olha isso https://site.com/promo")]
    [InlineData("entra em www.faculdade.com.br")]
    [InlineData("manda no portal.edu.br depois")]
    public void RejectsLinks(string text)
    {
        Assert.False(WarmupContentValidator.IsAcceptable(
            Conversation((true, text), (false, "blz")), out var reason));
        Assert.Equal("contém link", reason);
    }

    // Telefone, CPF ou matrícula: sequência longa de dígitos não aparece em
    // conversa de colega e cheira a dado vazado.
    [Theory]
    [InlineData("liga pro 11 98888-7777")]
    [InlineData("o cpf dele é 123.456.789-00")]
    public void RejectsLongDigitSequences(string text)
    {
        Assert.False(WarmupContentValidator.IsAcceptable(
            Conversation((true, text), (false, "ok")), out var reason));
        Assert.Contains("dígitos", reason);
    }

    // Qualquer coisa com cara de anúncio é recusada, mesmo sem link.
    [Theory]
    [InlineData("matricule já com desconto")]
    [InlineData("sai por R$ 199 no boleto")]
    [InlineData("clique aqui pra garantir")]
    public void RejectsAdvertising(string text)
    {
        Assert.False(WarmupContentValidator.IsAcceptable(
            Conversation((true, text), (false, "ok")), out var reason));
        Assert.Equal("tem cara de anúncio", reason);
    }

    // Parágrafo não é mensagem de WhatsApp entre colegas — é texto de anúncio
    // disfarçado de conversa.
    [Fact]
    public void RejectsLongMessages()
    {
        Assert.False(WarmupContentValidator.IsAcceptable(
            Conversation((true, new string('a', 200)), (false, "ok")), out var reason));
        Assert.Equal("mensagem longa demais", reason);
    }

    // Monólogo não gera o sinal de conversa de mão dupla, que é o motivo de a
    // feature existir.
    [Fact]
    public void RejectsOneSidedConversations()
    {
        Assert.False(WarmupContentValidator.IsAcceptable(
            Conversation((true, "oi"), (true, "tudo bem?")), out var reason));
        Assert.Equal("só um lado fala", reason);
    }

    // Turno vazio quebraria o envio lá na frente.
    [Fact]
    public void RejectsEmptyTurns()
    {
        Assert.False(WarmupContentValidator.IsAcceptable(
            Conversation((true, "oi"), (false, "   ")), out var reason));
        Assert.Equal("turno vazio", reason);
    }

    // Horário no meio da frase NÃO é telefone: "12h" e "as 14" são conversa
    // normal e não podem ser descartados (falso positivo custa geração à toa).
    [Theory]
    [InlineData("bora as 12h?")]
    [InlineData("chego 14h30")]
    public void DoesNotConfuseTimesWithPhoneNumbers(string text)
    {
        Assert.True(WarmupContentValidator.IsAcceptable(
            Conversation((true, text), (false, "beleza")), out _));
    }
}
