using MonitorVendas.Api.Features.Outcomes;

namespace MonitorVendas.Tests.Outcomes;

public class LabelNormalizerTests
{
    // Caixa, acento, emoji e espaços extras não podem separar a mesma etiqueta:
    // "Fechado ✅" e "fechado" precisam virar a mesma chave.
    [Theory]
    [InlineData("venda", "venda")]
    [InlineData("Venda", "venda")]
    [InlineData("VENDA ", "venda")]
    [InlineData("Venda ✅", "venda")]
    [InlineData("💰 Vendido!", "vendido")]
    [InlineData("Não fechou", "nao fechou")]
    [InlineData("cliente  perdido", "cliente perdido")]
    public void Normalize_RemovesCaseAccentAndSymbols(string input, string expected)
    {
        Assert.Equal(expected, LabelNormalizer.Normalize(input));
    }

    // Vazio, nulo ou só símbolos viram string vazia (não viram etiqueta válida).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("✅✅")]
    public void Normalize_EmptyInputs_ReturnEmpty(string? input)
    {
        Assert.Equal(string.Empty, LabelNormalizer.Normalize(input));
    }

    // Plural e variações continuam distintos — é por isso que cada um é cadastrado.
    [Fact]
    public void Normalize_KeepsWordsDistinct()
    {
        Assert.NotEqual(LabelNormalizer.Normalize("venda"), LabelNormalizer.Normalize("vendas"));
        Assert.NotEqual(LabelNormalizer.Normalize("venda"), LabelNormalizer.Normalize("venda cancelada"));
    }
}
