using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Conversations;

// O catálogo de etiquetas do WhatsApp chega por `labels.edit`. Ele é a ponte
// entre o id da etiqueta (que é o que vem na associação) e o nome que o usuário
// mapeia para um desfecho — errar aqui desliga o mapeamento inteiro.
public class LabelsEditTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000b1");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000b1");
    private static readonly Guid ContactId = Guid.Parse("c0a10000-0000-0000-0000-0000000000b1");
    private static readonly Guid ConversationId = Guid.Parse("c0117e00-0000-0000-0000-0000000000b1");
    private const string Instance = "mv-etiquetas";
    private const string ClientJid = "5511933332222@s.whatsapp.net";

    private async Task SeedAsync()
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900003333",
                InstanceName = Instance,
                Status = NumberStatus.Active,
                CreatedAt = Start,
            });
            db.Add(new Contact { Id = ContactId, RemoteJid = ClientJid, PushName = "Maria", CreatedAt = Start });
            db.Add(new Conversation
            {
                Id = ConversationId,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                ContactId = ContactId,
                StartedByContact = true,
                StartedAt = Start,
                LastMessageAt = Start,
            });

            return Task.CompletedTask;
        });
    }

    private async Task SendAsync(string body)
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Api.Features.Webhooks.IWebhookProcessor>().ProcessPendingAsync();
    }

    private Task EditAsync(string data) =>
        SendAsync($$"""{"event":"labels.edit","instance":"{{Instance}}","data":{{data}}}""");

    private Task AssociateAsync(string data) =>
        SendAsync($$"""{"event":"labels.association","instance":"{{Instance}}","data":{{data}}}""");

    private Task<List<WhatsappLabel>> LabelsAsync() =>
        InDbAsync(db => db.Set<WhatsappLabel>().AsNoTracking().ToListAsync());

    // Etiqueta nova é guardada com o nome; o mesmo evento repetido não duplica.
    [Fact]
    public async Task Edit_StoresTheLabelOnce()
    {
        await SeedAsync();

        await EditAsync("""{"labelId":"7","name":"Fechado","color":1}""");
        await EditAsync("""{"labelId":"7","name":"Fechado","color":1}""");

        var label = Assert.Single(await LabelsAsync());
        Assert.Equal("Fechado", label.Name);
    }

    // Renomear vale: a etiqueta renomeada pode passar a representar outro
    // desfecho, então o nome novo tem que valer daqui para frente.
    [Fact]
    public async Task Edit_RenamesAnExistingLabel()
    {
        await SeedAsync();
        await EditAsync("""{"labelId":"7","name":"Fechado"}""");

        await EditAsync("""{"labelId":"7","name":"Fechado com desconto"}""");

        var label = Assert.Single(await LabelsAsync());
        Assert.Equal("Fechado com desconto", label.Name);
    }

    // Etiqueta apagada no WhatsApp sai do catálogo — e apagar a que nem existe
    // não pode estourar.
    [Fact]
    public async Task Edit_WithDeletedFlag_RemovesTheLabel()
    {
        await SeedAsync();
        await EditAsync("""{"labelId":"7","name":"Fechado"}""");

        await EditAsync("""{"labelId":"7","name":"Fechado","deleted":true}""");
        await EditAsync("""{"labelId":"99","deleted":true}""");

        Assert.Empty(await LabelsAsync());
    }

    // Payload sem id ou sem nome é ignorado: etiqueta sem identidade não tem como
    // ser associada a nada depois.
    [Fact]
    public async Task Edit_WithoutIdOrName_IsIgnored()
    {
        await SeedAsync();

        await EditAsync("""{"name":"Sem id"}""");
        await EditAsync("""{"labelId":"8"}""");

        Assert.Empty(await LabelsAsync());
    }

    // Aplicar, remover e reaplicar a mesma etiqueta reaproveita o registro: é o
    // histórico que permite reavaliar o desfecho quando o catálogo muda.
    [Fact]
    public async Task Association_ReappliedAfterRemoval_ReusesTheRecord()
    {
        await SeedAsync();
        await EditAsync("""{"labelId":"7","name":"Fechado"}""");

        await AssociateAsync($$"""{"chatId":"{{ClientJid}}","labelId":"7","type":"add"}""");
        await AssociateAsync($$"""{"chatId":"{{ClientJid}}","labelId":"7","type":"remove"}""");

        var removed = Assert.Single(await InDbAsync(db => db.Set<ConversationLabel>().AsNoTracking().ToListAsync()));
        Assert.NotNull(removed.RemovedAt);

        await AssociateAsync($$"""{"chatId":"{{ClientJid}}","labelId":"7","type":"add"}""");

        var reapplied = Assert.Single(await InDbAsync(db => db.Set<ConversationLabel>().AsNoTracking().ToListAsync()));
        Assert.Null(reapplied.RemovedAt);
        Assert.Equal("Fechado", reapplied.LabelName);
    }

    // Remover etiqueta que nunca foi aplicada não cria registro nenhum — o
    // WhatsApp manda `remove` de coisas que não vimos aplicar.
    [Fact]
    public async Task Association_RemovingWhatWasNeverApplied_DoesNothing()
    {
        await SeedAsync();

        await AssociateAsync($$"""{"chatId":"{{ClientJid}}","labelId":"7","type":"remove"}""");

        Assert.Empty(await InDbAsync(db => db.Set<ConversationLabel>().AsNoTracking().ToListAsync()));
    }

    // Associação de conversa/contato que não existe, ou com tipo desconhecido, é
    // descartada sem quebrar a fila.
    [Fact]
    public async Task Association_WithUnknownChatOrType_IsIgnored()
    {
        await SeedAsync();

        await AssociateAsync("""{"chatId":"5511900009999@s.whatsapp.net","labelId":"7","type":"add"}""");
        await AssociateAsync($$"""{"chatId":"{{ClientJid}}","labelId":"7","type":"toggle"}""");
        await AssociateAsync($$"""{"chatId":"{{ClientJid}}","type":"add"}""");

        Assert.Empty(await InDbAsync(db => db.Set<ConversationLabel>().AsNoTracking().ToListAsync()));
    }

    // Alguns builds da Evolution aninham a associação em "association" — sem ler
    // esse formato, o desfecho simplesmente nunca chegaria.
    [Fact]
    public async Task Association_NestedInsideAssociation_IsRead()
    {
        await SeedAsync();
        await EditAsync("""{"labelId":"7","name":"Fechado"}""");

        await AssociateAsync($$"""{"association":{"chatId":"{{ClientJid}}","labelId":"7","type":"add"} }""");

        Assert.Single(await InDbAsync(db => db.Set<ConversationLabel>().AsNoTracking().ToListAsync()));
    }
}
