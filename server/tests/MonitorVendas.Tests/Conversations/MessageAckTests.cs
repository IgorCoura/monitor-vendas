using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Api.Features.Webhooks;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Conversations;

// Os acks alimentam a taxa de leitura. O payload da Evolution muda de forma
// entre versões (keyId, messageId, key.id; objeto ou array), e cada formato não
// tratado vira métrica de leitura silenciosamente menor.
public class MessageAckTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000ac");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000ac");
    private const string Instance = "mv-ack";

    private async Task SeedAsync()
    {
        await SeedAsync(db =>
        {
            db.Add(new Seller { Id = SellerId, Name = "Ana", Active = true, CreatedAt = Start });
            db.Add(new WhatsappNumber
            {
                Id = NumberId,
                SellerId = SellerId,
                Phone = "5511900001111",
                InstanceName = Instance,
                Status = NumberStatus.Active,
                CreatedAt = Start,
            });

            var contactId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();

            db.Add(new Contact { Id = contactId, RemoteJid = "5511977776666@s.whatsapp.net", PushName = "Maria", CreatedAt = Start });
            db.Add(new Conversation
            {
                Id = conversationId,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                ContactId = contactId,
                StartedByContact = false,
                StartedAt = Start,
                LastMessageAt = Start,
            });
            db.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                WhatsappNumberId = NumberId,
                SellerId = SellerId,
                WaMessageId = "OUT-1",
                Direction = MessageDirection.Outbound,
                Type = "conversation",
                Text = "bom dia",
                Timestamp = Start,
            });

            return Task.CompletedTask;
        });
    }

    private async Task SendUpdateAsync(object data)
    {
        var payload = JsonSerializer.Serialize(new { @event = "messages.update", instance = Instance, data });

        await Client.PostAsync($"/api/v1/webhooks/evolution/{IntegrationTestWebAppFactory.WebhookSecret}",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWebhookProcessor>().ProcessPendingAsync();
    }

    private Task<Message> MessageAsync() =>
        InDbAsync(db => db.Set<Message>().SingleAsync(m => m.WaMessageId == "OUT-1"));

    // Entregue marca só a entrega; a leitura continua em aberto.
    [Fact]
    public async Task DeliveryAck_MarksDeliveredOnly()
    {
        await SeedAsync();

        await SendUpdateAsync(new { keyId = "OUT-1", status = "DELIVERY_ACK" });

        var message = await MessageAsync();
        Assert.NotNull(message.DeliveredAt);
        Assert.Null(message.ReadAt);
    }

    // Lida implica entregue: o WhatsApp às vezes pula o ack de entrega, e sem
    // isso a mensagem ficaria "lida mas nunca entregue".
    [Fact]
    public async Task ReadAck_AlsoMarksDelivered()
    {
        await SeedAsync();

        await SendUpdateAsync(new { keyId = "OUT-1", status = "READ" });

        var message = await MessageAsync();
        Assert.NotNull(message.DeliveredAt);
        Assert.NotNull(message.ReadAt);
    }

    // O id da mensagem chega em três formatos diferentes conforme a versão.
    [Fact]
    public async Task Update_AcceptsMessageIdAndNestedKey()
    {
        await SeedAsync();
        await SendUpdateAsync(new { messageId = "OUT-1", status = "DELIVERY_ACK" });
        Assert.NotNull((await MessageAsync()).DeliveredAt);

        await SendUpdateAsync(new { key = new { id = "OUT-1" }, status = "READ" });
        Assert.NotNull((await MessageAsync()).ReadAt);
    }

    // A Evolution manda um objeto ou um array de updates na mesma rota.
    [Fact]
    public async Task Update_AcceptsAnArrayOfUpdates()
    {
        await SeedAsync();

        await SendUpdateAsync(new[] { new { keyId = "OUT-1", status = "READ" } });

        Assert.NotNull((await MessageAsync()).ReadAt);
    }

    // O primeiro ack manda: reenvio do mesmo evento não pode mover a hora da
    // leitura para frente e encolher a espera medida.
    [Fact]
    public async Task RepeatedAck_KeepsTheFirstTimestamp()
    {
        await SeedAsync();
        await SendUpdateAsync(new { keyId = "OUT-1", status = "READ" });
        var first = await MessageAsync();

        await SendUpdateAsync(new { keyId = "OUT-1", status = "READ" });
        var second = await MessageAsync();

        Assert.Equal(first.ReadAt, second.ReadAt);
        Assert.Equal(first.DeliveredAt, second.DeliveredAt);
    }

    // Status desconhecido, id inexistente ou payload incompleto são ignorados —
    // nada muda e nada estoura.
    [Fact]
    public async Task Update_IgnoresUnknownStatusAndMissingMessage()
    {
        await SeedAsync();

        await SendUpdateAsync(new { keyId = "OUT-1", status = "PENDING" });
        await SendUpdateAsync(new { keyId = "NAO-EXISTE", status = "READ" });
        await SendUpdateAsync(new { status = "READ" });
        await SendUpdateAsync(new { keyId = "OUT-1" });

        var message = await MessageAsync();
        Assert.Null(message.DeliveredAt);
        Assert.Null(message.ReadAt);
    }
}
