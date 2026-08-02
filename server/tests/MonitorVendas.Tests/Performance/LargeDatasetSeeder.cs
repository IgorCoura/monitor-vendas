using MonitorVendas.Api.Data;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Outcomes;
using MonitorVendas.Api.Features.Sellers;

namespace MonitorVendas.Tests.Performance;

// Semeia uma base realista direto no banco (sem passar por webhook) para medir
// o custo dos relatórios. Determinístico: mesmo shape em toda execução.
public static class LargeDatasetSeeder
{
    public sealed record Shape(int Sellers, int NumbersPerSeller, int Days, int ConversationsPerNumberPerDay, int MessagesPerConversation)
    {
        public static Shape Default => new(Sellers: 10, NumbersPerSeller: 2, Days: 90, ConversationsPerNumberPerDay: 2, MessagesPerConversation: 8);

        public int TotalMessages => Sellers * NumbersPerSeller * Days * ConversationsPerNumberPerDay * MessagesPerConversation;
    }

    public static async Task<(List<Guid> SellerIds, int Messages)> SeedAsync(AppDbContext db, DateTime periodEndUtc, Shape shape)
    {
        var sellerIds = new List<Guid>();
        var sellers = new List<Seller>();
        var numbers = new List<WhatsappNumber>();
        var contacts = new List<Contact>();
        var conversations = new List<Conversation>();
        var messages = new List<Message>();
        var statusEvents = new List<NumberStatusEvent>();
        var outcomes = new List<ConversationOutcome>();

        var start = periodEndUtc.AddDays(-shape.Days);

        for (var s = 0; s < shape.Sellers; s++)
        {
            var seller = new Seller
            {
                Id = Guid.NewGuid(),
                Name = $"Vendedor {s:D2}",
                Active = true,
                CreatedAt = start.AddDays(-1),
            };
            sellers.Add(seller);
            sellerIds.Add(seller.Id);

            for (var n = 0; n < shape.NumbersPerSeller; n++)
            {
                var number = new WhatsappNumber
                {
                    Id = Guid.NewGuid(),
                    SellerId = seller.Id,
                    Phone = $"55119{s:D2}{n:D2}0000",
                    InstanceName = $"bench-{s:D2}-{n:D2}",
                    Status = NumberStatus.Active,
                    CreatedAt = start.AddDays(-1),
                };
                numbers.Add(number);

                // Conexão inicial + uma queda de 1 dia no meio do período.
                statusEvents.Add(new NumberStatusEvent
                {
                    WhatsappNumberId = number.Id,
                    State = "open",
                    StatusReason = 200,
                    ResultingStatus = NumberStatus.Active,
                    OccurredAt = start.AddHours(-1),
                });
                statusEvents.Add(new NumberStatusEvent
                {
                    WhatsappNumberId = number.Id,
                    State = "close",
                    StatusReason = 403,
                    ResultingStatus = NumberStatus.BannedTemporary,
                    OccurredAt = start.AddDays(shape.Days / 2.0),
                });
                statusEvents.Add(new NumberStatusEvent
                {
                    WhatsappNumberId = number.Id,
                    State = "open",
                    StatusReason = 200,
                    ResultingStatus = NumberStatus.Active,
                    OccurredAt = start.AddDays(shape.Days / 2.0 + 1),
                });

                for (var d = 0; d < shape.Days; d++)
                {
                    for (var c = 0; c < shape.ConversationsPerNumberPerDay; c++)
                    {
                        // 13h UTC = 10h em São Paulo (dentro do expediente).
                        var startedAt = start.AddDays(d).Date.AddHours(13).AddMinutes(c * 90);
                        var contact = new Contact
                        {
                            Id = Guid.NewGuid(),
                            RemoteJid = $"5511{s:D2}{n:D2}{d:D3}{c:D2}@s.whatsapp.net",
                            PushName = $"Cliente {d:D3}-{c:D2}",
                            CreatedAt = startedAt,
                        };
                        contacts.Add(contact);

                        var startedByContact = (d + c) % 3 != 0;
                        var conversation = new Conversation
                        {
                            Id = Guid.NewGuid(),
                            WhatsappNumberId = number.Id,
                            SellerId = number.SellerId,
                            ContactId = contact.Id,
                            StartedByContact = startedByContact,
                            StartedAt = startedAt,
                            LastMessageAt = startedAt.AddMinutes(shape.MessagesPerConversation * 7),
                        };
                        conversations.Add(conversation);

                        for (var m = 0; m < shape.MessagesPerConversation; m++)
                        {
                            var inbound = startedByContact ? m % 2 == 0 : m % 2 == 1;
                            var timestamp = startedAt.AddMinutes(m * 7);
                            messages.Add(new Message
                            {
                                Id = Guid.NewGuid(),
                                ConversationId = conversation.Id,
                                WhatsappNumberId = number.Id,
                                SellerId = number.SellerId,
                                WaMessageId = $"BM-{number.Id:N}-{d:D3}-{c:D2}-{m:D2}",
                                Direction = inbound ? MessageDirection.Inbound : MessageDirection.Outbound,
                                Type = "conversation",
                                Text = "mensagem de benchmark",
                                Timestamp = timestamp,
                                DeliveredAt = inbound ? null : timestamp.AddMinutes(1),
                                ReadAt = inbound || m % 3 != 0 ? null : timestamp.AddMinutes(2),
                            });
                        }

                        // ~1 em cada 5 conversas vira venda.
                        if ((d + c) % 5 == 0)
                        {
                            outcomes.Add(new ConversationOutcome
                            {
                                Id = Guid.NewGuid(),
                                ConversationId = conversation.Id,
                                OutcomeTypeCode = OutcomeTypeCodes.Sale,
                                LabelId = "lbl-venda",
                                MarkedAt = startedAt.AddHours(2),
                            });
                        }
                    }
                }
            }
        }

        db.AddRange(sellers);
        db.AddRange(numbers);
        db.AddRange(statusEvents);
        await db.SaveChangesAsync();

        db.AddRange(contacts);
        await db.SaveChangesAsync();
        db.AddRange(conversations);
        await db.SaveChangesAsync();

        foreach (var batch in messages.Chunk(5000))
        {
            db.AddRange(batch);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        db.AddRange(outcomes);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return (sellerIds, messages.Count);
    }
}
