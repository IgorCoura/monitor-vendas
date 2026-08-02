using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MonitorVendas.Api.Features.Contacts;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Metrics;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Metrics;

// O vínculo vendedor↔número é histórico: o passado pertence a quem atendeu, e
// transferir o número não pode reescrever mês fechado.
public class NumberTransferTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime PeriodEnd = new(2026, 7, 30, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodStart = PeriodEnd.AddDays(-5);

    private static readonly Guid AnaId = Guid.Parse("5e11e000-0000-0000-0000-0000000000a1");
    private static readonly Guid BrunoId = Guid.Parse("5e11e000-0000-0000-0000-0000000000b1");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000a1");

    private string Range => $"from={PeriodStart:O}&to={PeriodEnd:O}";

    private async Task SeedAsync()
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = AnaId, Name = "Ana", Active = true, CreatedAt = PeriodStart });
            db.Add(new Seller { Id = BrunoId, Name = "Bruno", Active = true, CreatedAt = PeriodStart });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = AnaId,
                Phone = "5511900001111",
                InstanceName = "mv-transfer",
                Status = NumberStatus.Active,
                CreatedAt = PeriodStart,
            });

            var contactId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            var start = PeriodStart.AddDays(1);

            db.Add(new Contact { Id = contactId, RemoteJid = "5511977778888@s.whatsapp.net", PushName = "Cliente", CreatedAt = start });
            db.Add(new Conversation
            {
                Id = conversationId,
                WhatsappNumberId = NumberId,
                SellerId = AnaId,
                ContactId = contactId,
                StartedByContact = true,
                StartedAt = start,
                LastMessageAt = start.AddMinutes(20),
            });
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                WhatsappNumberId = NumberId,
                SellerId = AnaId,
                WaMessageId = "t-1",
                Direction = MessageDirection.Inbound,
                Type = "conversation",
                Text = "quanto custa?",
                Timestamp = start,
            });
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                WhatsappNumberId = NumberId,
                SellerId = AnaId,
                WaMessageId = "t-2",
                Direction = MessageDirection.Outbound,
                Type = "conversation",
                Text = "R$ 200",
                Timestamp = start.AddMinutes(20),
            });

            return Task.CompletedTask;
        });
    }

    private async Task<RankingEntryDto> RankingOfAsync(Guid sellerId)
    {
        var ranking = await Client.GetFromJsonAsync<List<RankingEntryDto>>($"/api/v1/reports/ranking?{Range}");
        return ranking!.Single(r => r.SellerId == sellerId);
    }

    // Transferir o número não move o que já aconteceu: a conversa atendida pela
    // Ana continua dela, e o Bruno começa zerado no período.
    [Fact]
    public async Task Transfer_DoesNotMoveThePast()
    {
        await SeedAsync();

        var antes = await RankingOfAsync(AnaId);
        Assert.Equal(1, antes.Metrics.ConversationsStarted);

        var response = await Client.PostAsJsonAsync($"/api/v1/numbers/{NumberId}/transfer", new { sellerId = BrunoId });
        response.EnsureSuccessStatusCode();

        var ana = await RankingOfAsync(AnaId);
        var bruno = await RankingOfAsync(BrunoId);

        Assert.Equal(1, ana.Metrics.ConversationsStarted);
        Assert.Equal(0, bruno.Metrics.ConversationsStarted);
    }

    // O relatório do vendedor antigo continua listando o número transferido: ele
    // respondeu por aquilo, e sumir da tela seria esconder o próprio histórico.
    [Fact]
    public async Task SellerReport_KeepsTheTransferredNumberForThePreviousOwner()
    {
        await SeedAsync();
        await Client.PostAsJsonAsync($"/api/v1/numbers/{NumberId}/transfer", new { sellerId = BrunoId });

        var relatorio = await Client.GetFromJsonAsync<SellerReportDto>($"/api/v1/reports/sellers/{AnaId}?{Range}");

        var numero = Assert.Single(relatorio!.Numbers);
        Assert.Equal(NumberId, numero.NumberId);
        Assert.Equal(1, relatorio.Totals.ConversationsStarted);
    }

    // O que vier depois da transferência é do dono novo: mensagem nova carimba
    // Bruno, e é ele quem passa a contar.
    [Fact]
    public async Task AfterTransfer_NewActivityBelongsToTheNewOwner()
    {
        await SeedAsync();
        await Client.PostAsJsonAsync($"/api/v1/numbers/{NumberId}/transfer", new { sellerId = BrunoId });

        await SeedAsync(db =>
        {
            var contactId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            var start = PeriodEnd.AddHours(-2);

            db.Add(new Contact { Id = contactId, RemoteJid = "5511966660000@s.whatsapp.net", PushName = "Novo", CreatedAt = start });
            db.Add(new Conversation
            {
                Id = conversationId,
                WhatsappNumberId = NumberId,
                SellerId = BrunoId,
                ContactId = contactId,
                StartedByContact = true,
                StartedAt = start,
                LastMessageAt = start,
            });
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                WhatsappNumberId = NumberId,
                SellerId = BrunoId,
                WaMessageId = "t-3",
                Direction = MessageDirection.Inbound,
                Type = "conversation",
                Text = "oi",
                Timestamp = start,
            });

            return Task.CompletedTask;
        });

        var ana = await RankingOfAsync(AnaId);
        var bruno = await RankingOfAsync(BrunoId);

        Assert.Equal(1, ana.Metrics.ConversationsStarted);
        Assert.Equal(1, bruno.Metrics.ConversationsStarted);
    }

    // O contato aparece sob quem o atendeu, não sob o dono atual do número.
    [Fact]
    public async Task Contacts_AreListedUnderTheSellerWhoHandledThem()
    {
        await SeedAsync();
        await Client.PostAsJsonAsync($"/api/v1/numbers/{NumberId}/transfer", new { sellerId = BrunoId });

        var daAna = await Client.GetFromJsonAsync<ContactPageDto>($"/api/v1/contacts?{Range}&sellerId={AnaId}");
        var doBruno = await Client.GetFromJsonAsync<ContactPageDto>($"/api/v1/contacts?{Range}&sellerId={BrunoId}");

        Assert.Equal(1, daAna!.Total);
        Assert.Equal("Ana", daAna.Items[0].SellerName);
        Assert.Equal(0, doBruno!.Total);
    }
}
