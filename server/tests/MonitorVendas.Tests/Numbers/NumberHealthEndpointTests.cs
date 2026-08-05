using System.Net.Http.Json;
using System.Text.Json;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Numbers;

public class NumberHealthEndpointTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000dd");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000dd");
    private static readonly DateTime Now = DateTime.UtcNow;

    private async Task SeedNumberAsync(Action<MonitorVendas.Api.Data.AppDbContext>? extra = null)
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Now.AddDays(-30) });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900003333",
                InstanceName = "mv-health",
                Status = NumberStatus.Active,
                CreatedAt = Now.AddDays(-30),
            });
            extra?.Invoke(db);
            return Task.CompletedTask;
        });
    }

    // Envia mensagens antigas o bastante para contarem na taxa de entrega (a
    // janela de 15 min protege as recém-enviadas).
    private static void AddOutbound(MonitorVendas.Api.Data.AppDbContext db, int delivered, int undelivered)
    {
        var contact = new Contact { Id = Guid.NewGuid(), RemoteJid = "5511700009999@s.whatsapp.net", CreatedAt = Now.AddDays(-3) };
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            WhatsappNumberId = NumberId,
            SellerId = SellerId,
            ContactId = contact.Id,
            StartedByContact = true,
            StartedAt = Now.AddDays(-2),
            LastMessageAt = Now.AddHours(-1),
        };
        db.Add(contact);
        db.Add(conversation);

        for (var i = 0; i < delivered + undelivered; i++)
        {
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                WaMessageId = $"H-{i}",
                Direction = MessageDirection.Outbound,
                Type = "conversation",
                Timestamp = Now.AddHours(-2).AddMinutes(i),
                DeliveredAt = i < delivered ? Now.AddHours(-2).AddMinutes(i + 1) : null,
            });
        }
    }

    // Número com metade das mensagens sem entrega aparece com o sinal `delivery`
    // e nível pelo menos médio — é o soft-ban visível antes do ban.
    [Fact]
    public async Task Health_WithDegradedDelivery_FlagsTheNumber()
    {
        await SeedNumberAsync(db => AddOutbound(db, delivered: 5, undelivered: 5));

        var health = await Client.GetFromJsonAsync<JsonElement>("/api/v1/numbers/health");

        var row = Assert.Single(health.EnumerateArray().ToList());
        Assert.Equal("Ana", row.GetProperty("sellerName").GetString());
        Assert.True(row.GetProperty("score").GetInt32() >= 30);
        Assert.Equal("Medium", row.GetProperty("level").GetString());
        var signals = row.GetProperty("signals").EnumerateArray().ToList();
        Assert.Contains(signals, s => s.GetProperty("key").GetString() == "delivery" && s.GetProperty("value").GetString() == "50%");
    }

    // Número recém-cadastrado, sem tráfego, vem como NoData — nunca como alarme.
    [Fact]
    public async Task Health_WithoutTraffic_IsNoData()
    {
        await SeedNumberAsync();

        var health = await Client.GetFromJsonAsync<JsonElement>("/api/v1/numbers/health");

        var row = Assert.Single(health.EnumerateArray().ToList());
        Assert.Equal("NoData", row.GetProperty("level").GetString());
        Assert.Equal(0, row.GetProperty("score").GetInt32());
    }

    // O ban (statusReason 403) dentro da janela pesa 40 pontos no score.
    [Fact]
    public async Task Health_WithBanInPeriod_FlagsTheBan()
    {
        await SeedNumberAsync(db => db.Add(new NumberStatusEvent
        {
            WhatsappNumberId = NumberId,
            State = "close",
            StatusReason = 403,
            ResultingStatus = NumberStatus.BannedTemporary,
            OccurredAt = Now.AddDays(-1),
        }));

        var health = await Client.GetFromJsonAsync<JsonElement>("/api/v1/numbers/health");

        var row = Assert.Single(health.EnumerateArray().ToList());
        Assert.Equal(40, row.GetProperty("score").GetInt32());
        Assert.Contains(row.GetProperty("signals").EnumerateArray(),
            s => s.GetProperty("key").GetString() == "ban");
    }
}
