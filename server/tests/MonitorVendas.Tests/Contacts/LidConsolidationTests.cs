using MonitorVendas.Api.Features.Contacts;

namespace MonitorVendas.Tests.Contacts;

// O WhatsApp passou a endereçar por LID, e o `remoteJid` nesse modo não tem
// telefone. O que já entrou assim precisa voltar a ser um contato só, com número
// — mas nunca à custa de inventar telefone.
public class LidConsolidationTests
{
    private static LidContactRow Lid(string jid, int conversations = 1) =>
        new(Guid.NewGuid(), jid, conversations);

    // LID conhecido e sem cadastro por telefone: o próprio contato vira o de
    // telefone, preservando o histórico inteiro sem mover nada.
    [Fact]
    public void KnownLidWithoutATwin_IsRenamedInPlace()
    {
        var lid = Lid("111@lid");

        var plan = LidConsolidationPlanner.Plan(
            [lid],
            new Dictionary<string, string> { ["111@lid"] = "5511999998888@s.whatsapp.net" },
            new Dictionary<string, Guid>());

        var rename = Assert.Single(plan.Renames);
        Assert.Equal(lid.Id, rename.ContactId);
        Assert.Equal("5511999998888@s.whatsapp.net", rename.PhoneJid);
        Assert.Empty(plan.Merges);
    }

    // Já existe o cadastro por telefone: as conversas do LID passam para ele.
    [Fact]
    public void KnownLidWithATwin_IsMergedIntoThePhoneContact()
    {
        var lid = Lid("111@lid", conversations: 3);
        var phoneId = Guid.NewGuid();

        var plan = LidConsolidationPlanner.Plan(
            [lid],
            new Dictionary<string, string> { ["111@lid"] = "5511999998888@s.whatsapp.net" },
            new Dictionary<string, Guid> { ["5511999998888@s.whatsapp.net"] = phoneId });

        var merge = Assert.Single(plan.Merges);
        Assert.Equal(lid.Id, merge.LidContactId);
        Assert.Equal(phoneId, merge.TargetContactId);
        Assert.Equal(3, merge.Conversations);
        Assert.Empty(plan.Renames);
    }

    // O LID NÃO é reversível: sem ter visto o par num payload, o contato fica
    // como está e é reportado. Inventar telefone é pior que dado incompleto.
    [Fact]
    public void UnknownLid_IsReportedAndLeftAlone()
    {
        var plan = LidConsolidationPlanner.Plan(
            [Lid("999@lid")],
            new Dictionary<string, string>(),
            new Dictionary<string, Guid>());

        Assert.Equal("999@lid", Assert.Single(plan.Unresolved));
        Assert.Empty(plan.Renames);
        Assert.Empty(plan.Merges);
    }

    // Dois LIDs para o MESMO telefone: o primeiro fica com o cadastro e o
    // segundo funde nele — senão o rename do segundo criaria duas linhas com o
    // mesmo JID.
    [Fact]
    public void TwoLidsForTheSamePhone_ConvergeIntoOneContact()
    {
        var busier = Lid("111@lid", conversations: 5);
        var quieter = Lid("222@lid", conversations: 1);

        var plan = LidConsolidationPlanner.Plan(
            [quieter, busier],
            new Dictionary<string, string>
            {
                ["111@lid"] = "5511999998888@s.whatsapp.net",
                ["222@lid"] = "5511999998888@s.whatsapp.net",
            },
            new Dictionary<string, Guid>());

        // Quem tem mais conversa fica com o cadastro.
        Assert.Equal(busier.Id, Assert.Single(plan.Renames).ContactId);
        var merge = Assert.Single(plan.Merges);
        Assert.Equal(quieter.Id, merge.LidContactId);
        Assert.Equal(busier.Id, merge.TargetContactId);
    }

    // O plano não pode mudar entre a prévia e o apply: mesma entrada, mesma
    // saída, independentemente da ordem em que os contatos vieram do banco.
    [Fact]
    public void ThePlanIsDeterministic()
    {
        var a = Lid("111@lid", 2);
        var b = Lid("222@lid", 2);
        var map = new Dictionary<string, string>
        {
            ["111@lid"] = "5511111111111@s.whatsapp.net",
            ["222@lid"] = "5522222222222@s.whatsapp.net",
        };

        var first = LidConsolidationPlanner.Plan([a, b], map, new Dictionary<string, Guid>());
        var second = LidConsolidationPlanner.Plan([b, a], map, new Dictionary<string, Guid>());

        Assert.Equal(
            first.Renames.Select(r => r.PhoneJid),
            second.Renames.Select(r => r.PhoneJid));
    }

    // Nada gravado por LID: plano vazio, e o apply não tem o que fazer.
    [Fact]
    public void WithoutAnyLidContact_ThePlanIsEmpty()
    {
        var plan = LidConsolidationPlanner.Plan([], new Dictionary<string, string>(), new Dictionary<string, Guid>());

        Assert.Empty(plan.Renames);
        Assert.Empty(plan.Merges);
        Assert.Empty(plan.Unresolved);
    }
}
