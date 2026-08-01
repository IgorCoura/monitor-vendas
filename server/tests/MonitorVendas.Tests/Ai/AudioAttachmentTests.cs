using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MonitorVendas.Api.Features.Ai.Analysis;
using MonitorVendas.Api.Features.Conversations;
using MonitorVendas.Api.Features.Numbers;
using MonitorVendas.Api.Features.Sellers;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Ai;

// O caminho do áudio até o modelo: buscar na Evolution, numerar na transcrição e
// degradar sem derrubar a análise. Era o trecho de produção sem teste nenhum, e
// foi justamente onde uma falha silenciosa passou por "a IA não entendeu".
public class AudioAttachmentTests(IntegrationTestWebAppFactory factory) : BaseIntegrationTest(factory)
{
    private static readonly DateTime Start = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SellerId = Guid.Parse("5e11e000-0000-0000-0000-0000000000ad");
    private static readonly Guid NumberId = Guid.Parse("f0a10000-0000-0000-0000-0000000000ad");
    private const string Instance = "mv-audio";

    private async Task SeedAsync(params (string WaId, int? Seconds, string Type)[] messages)
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
                StartedByContact = true,
                StartedAt = Start,
                LastMessageAt = Start.AddMinutes(messages.Length),
            });

            for (var i = 0; i < messages.Length; i++)
            {
                var (waId, seconds, type) = messages[i];
                db.Add(new Message
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversationId,
                    WhatsappNumberId = NumberId,
                    SellerId = SellerId,
                    WaMessageId = waId,
                    Direction = MessageDirection.Inbound,
                    Type = type,
                    Text = type == "conversation" ? "texto" : null,
                    DurationSeconds = seconds,
                    Timestamp = Start.AddMinutes(i),
                });
            }

            return Task.CompletedTask;
        });
    }

    private async Task<ConversationContext> LoadAsync(bool includeAudio)
    {
        using var scope = Factory.Services.CreateScope();
        var workset = scope.ServiceProvider.GetRequiredService<ConversationAiWorkset>();
        var (items, _) = await workset.LoadAsync(
            new ConversationAiFilter(Start.AddDays(-1), Start.AddDays(1), [], 100, Force: true, IncludeAudio: includeAudio));

        return Assert.Single(items);
    }

    private void RespondWithAudio(string base64 = "T2dnUw==") =>
        FakeEvolution.When(HttpMethod.Post, "/chat/getBase64FromMediaMessage/",
            $$"""{"mediaType":"audioMessage","mimetype":"audio/ogg; codecs=opus","base64":"{{base64}}"}""");

    // Com o áudio ligado, cada anexo é numerado e o marcador da transcrição
    // aponta para ele — sem isso o modelo recebe blobs soltos.
    [Fact]
    public async Task LoadAsync_WithAudio_NumbersTheAttachmentsInOrder()
    {
        await SeedAsync(("a-1", 45, "audioMessage"), ("t-1", null, "conversation"), ("a-2", 12, "pttMessage"));
        RespondWithAudio();

        var conversation = await LoadAsync(includeAudio: true);

        Assert.Equal(2, conversation.Input.Attachments!.Count);
        Assert.Equal(2, conversation.Input.AudioExpected);
        Assert.Equal(57, conversation.AudioSeconds);
        Assert.Contains("[áudio 1 de 45s]", conversation.Input.Transcript);
        Assert.Contains("[áudio 2 de 12s]", conversation.Input.Transcript);
        Assert.Equal("audio/ogg; codecs=opus", conversation.Input.Attachments[0].MimeType);
    }

    // Áudio desligado: nada é baixado e o marcador fica sem número — não há
    // anexo para o modelo procurar.
    [Fact]
    public async Task LoadAsync_WithoutAudio_DownloadsNothing()
    {
        await SeedAsync(("a-1", 45, "audioMessage"));

        var conversation = await LoadAsync(includeAudio: false);

        Assert.Null(conversation.Input.Attachments);
        Assert.Equal(0, conversation.Input.AudioExpected);
        Assert.Contains("[áudio de 45s]", conversation.Input.Transcript);
        Assert.DoesNotContain(FakeEvolution.Requests, r => r.Path.Contains("getBase64FromMediaMessage"));
    }

    // Falha no download degrada para o marcador: áudio é enriquecimento, nunca
    // pode derrubar a leitura da conversa.
    [Fact]
    public async Task LoadAsync_WhenDownloadFails_KeepsTheConversationWithoutTheAudio()
    {
        await SeedAsync(("a-1", 45, "audioMessage"));
        FakeEvolution.When(HttpMethod.Post, "/chat/getBase64FromMediaMessage/", "{}", HttpStatusCode.InternalServerError);

        var conversation = await LoadAsync(includeAudio: true);

        Assert.Null(conversation.Input.Attachments);
        // O par esperado/anexado é o que denuncia a leitura incompleta.
        Assert.Equal(1, conversation.Input.AudioExpected);
        Assert.Contains("[áudio de 45s]", conversation.Input.Transcript);
    }

    // Resposta sem o base64 (mídia expirada na Evolution) conta como falha.
    [Fact]
    public async Task LoadAsync_WhenResponseHasNoBase64_TreatsAsFailure()
    {
        await SeedAsync(("a-1", 45, "audioMessage"));
        FakeEvolution.When(HttpMethod.Post, "/chat/getBase64FromMediaMessage/", """{"mediaType":"audioMessage"}""");

        var conversation = await LoadAsync(includeAudio: true);

        Assert.Null(conversation.Input.Attachments);
        Assert.Equal(1, conversation.Input.AudioExpected);
    }

    // O teto por conversa existe para um áudio de 30 minutos não valer ~57 mil
    // tokens sozinho: o que passa dele fica só como marcador.
    [Fact]
    public async Task LoadAsync_StopsAtTheAudioCap()
    {
        await SeedAsync(("a-1", 200, "audioMessage"), ("a-2", 200, "audioMessage"));
        RespondWithAudio();

        using var host = Factory.WithWebHostBuilder(b => b.UseSetting("Ai:MaxAudioSecondsPerConversation", "250"));
        using var scope = host.Services.CreateScope();
        var workset = scope.ServiceProvider.GetRequiredService<ConversationAiWorkset>();
        var (items, _) = await workset.LoadAsync(
            new ConversationAiFilter(Start.AddDays(-1), Start.AddDays(1), [], 100, Force: true, IncludeAudio: true));

        var conversation = Assert.Single(items);
        Assert.Single(conversation.Input.Attachments!);
        Assert.Equal(2, conversation.Input.AudioExpected);
        Assert.Equal(200, conversation.AudioSeconds);
    }

    // Só áudio vira anexo: imagem e vídeo continuam como marcador na transcrição.
    [Fact]
    public async Task LoadAsync_OnlyAttachesAudioMessages()
    {
        await SeedAsync(("i-1", null, "imageMessage"), ("v-1", 30, "videoMessage"), ("a-1", 5, "audioMessage"));
        RespondWithAudio();

        var conversation = await LoadAsync(includeAudio: true);

        Assert.Single(conversation.Input.Attachments!);
        Assert.Equal(1, conversation.Input.AudioExpected);
        Assert.Contains("[imagem]", conversation.Input.Transcript);
        Assert.Contains("[vídeo de 30s]", conversation.Input.Transcript);
        Assert.Contains("[áudio 1 de 5s]", conversation.Input.Transcript);
    }
}
