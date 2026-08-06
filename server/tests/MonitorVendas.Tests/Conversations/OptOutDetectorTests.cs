using MonitorVendas.Api.Features.Conversations;

namespace MonitorVendas.Tests.Conversations;

public class OptOutDetectorTests
{
    // Pedidos inequívocos de descadastro, com e sem acento/pontuação.
    [Theory]
    [InlineData("SAIR")]
    [InlineData("sair")]
    [InlineData("Pare!")]
    [InlineData("parar")]
    [InlineData("Não quero mais receber")]
    [InlineData("me remova")]
    [InlineData("STOP")]
    public void RecognizesOptOutRequests(string text)
    {
        Assert.True(OptOutDetector.IsOptOut(text));
    }

    // Conversa normal de venda NÃO vira opt-out: um falso positivo aqui silencia
    // um cliente ativo, que é erro pior que deixar passar.
    [Theory]
    [InlineData("não quero o azul, quero o vermelho")]
    [InlineData("para quando fica pronto?")]
    [InlineData("vou parar na loja amanhã pra buscar, pode separar?")]
    [InlineData("bom dia, tudo bem?")]
    [InlineData("")]
    [InlineData(null)]
    public void DoesNotFlagNormalConversation(string? text)
    {
        Assert.False(OptOutDetector.IsOptOut(text));
    }

    // Num texto longo só a frase completa conta: "pare" solto no meio de um
    // parágrafo quase sempre é outra coisa.
    [Fact]
    public void InLongText_OnlyFullPhrasesCount()
    {
        const string longText = "Oi, tudo bem? Recebi o produto e gostei muito, mas queria saber se pare"
            + " de fabricar esse modelo vocês avisam os clientes antigos?";

        Assert.False(OptOutDetector.IsOptOut(longText));
        Assert.True(OptOutDetector.IsOptOut(
            "Boa tarde, por gentileza nao quero mais receber essas mensagens de vocês, obrigado."));
    }
}
