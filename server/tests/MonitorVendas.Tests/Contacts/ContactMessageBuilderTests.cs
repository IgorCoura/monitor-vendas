using MonitorVendas.Api.Features.Contacts;

namespace MonitorVendas.Tests.Contacts;

public class ContactMessageBuilderTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    // 00:00 e 23:59 locais de 01/07 e 30/07.
    private static readonly DateTime From = new(2026, 7, 1, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 7, 31, 2, 59, 59, DateTimeKind.Utc);

    private static ContactRowDto Row(string name, string phone) =>
        new(Guid.NewGuid(), name, phone, From, To, null, null, [], null, null, null, "Active", false);

    // Cada contato vira "Nome - número" e o cabeçalho traz o período em horário local.
    [Fact]
    public void Build_FormatsLinesAndHeader()
    {
        var rows = new[] { Row("Maria Silva", "5511988887777"), Row("João Souza", "5511977776666") };

        var messages = ContactMessageBuilder.Build(rows, From, To, SaoPaulo, 3500);

        Assert.Equal(
            "Contatos — 01/07 a 30/07\n\nMaria Silva - 5511988887777\nJoão Souza - 5511977776666",
            Assert.Single(messages));
    }

    // Contato sem nome salvo (nome = telefone) sai só com o número, não "5511 - 5511".
    [Fact]
    public void Build_ContactWithoutName_ShowsOnlyPhone()
    {
        var messages = ContactMessageBuilder.Build([Row("5511988887777", "5511988887777")], From, To, SaoPaulo, 3500);

        Assert.EndsWith("\n\n5511988887777", Assert.Single(messages));
    }

    // Passando do limite, a lista é quebrada em blocos numerados — sem perder contato.
    [Fact]
    public void Build_SplitsIntoNumberedBlocks()
    {
        var rows = Enumerable.Range(0, 40).Select(i => Row($"Cliente {i:D2}", $"55119000{i:D5}")).ToList();

        var messages = ContactMessageBuilder.Build(rows, From, To, SaoPaulo, 300);

        Assert.True(messages.Count > 1);
        Assert.StartsWith("Contatos (1/", messages[0]);
        Assert.StartsWith($"Contatos ({messages.Count}/{messages.Count})", messages[^1]);
        Assert.All(messages, m => Assert.True(m.Length <= 300, $"bloco com {m.Length} caracteres"));
        Assert.All(rows, r => Assert.Contains(messages, m => m.Contains(r.Phone)));
    }

    // Bloco único não leva contador: "Contatos (1/1)" seria ruído.
    [Fact]
    public void Build_SingleBlock_HasNoCounter()
    {
        var messages = ContactMessageBuilder.Build([Row("Maria", "5511988887777")], From, To, SaoPaulo, 3500);

        Assert.DoesNotContain("(1/1)", Assert.Single(messages));
    }

    // Contato cujo nome sozinho estoura o limite vai numa mensagem própria em vez
    // de ser descartado (nenhum contato pode sumir do envio).
    [Fact]
    public void Build_OversizedLine_GetsItsOwnMessage()
    {
        var rows = new[]
        {
            Row("Maria", "5511988887777"),
            Row(new string('A', 400), "5511977776666"),
            Row("João", "5511966665555"),
        };

        var messages = ContactMessageBuilder.Build(rows, From, To, SaoPaulo, 300);

        Assert.Equal(3, messages.Count);
        Assert.All(rows, r => Assert.Contains(messages, m => m.Contains(r.Phone)));
    }

    // Sem período informado, o cabeçalho não inventa datas.
    [Fact]
    public void Build_WithoutPeriod_OmitsDates()
    {
        var messages = ContactMessageBuilder.Build([Row("Maria", "5511988887777")], null, null, SaoPaulo, 3500);

        Assert.StartsWith("Contatos\n\n", Assert.Single(messages));
    }

    // Lista vazia não gera mensagem nenhuma.
    [Fact]
    public void Build_EmptyList_ReturnsNothing()
    {
        Assert.Empty(ContactMessageBuilder.Build([], From, To, SaoPaulo, 3500));
    }
}
