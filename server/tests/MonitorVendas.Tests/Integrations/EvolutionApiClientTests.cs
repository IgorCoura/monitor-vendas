using System.Net;
using MonitorVendas.Api.Integrations.Evolution;

namespace MonitorVendas.Tests.Integrations;

public class EvolutionApiClientTests
{
    private static (EvolutionApiClient Client, RecordingHandler Handler) Build(HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var handler = new RecordingHandler { StatusCode = status, Body = body };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://evolution.local/") };
        return (new EvolutionApiClient(http), handler);
    }

    // Enviar texto faz POST em message/sendText/{instance} com number e text no corpo JSON.
    [Fact]
    public async Task SendTextAsync_PostsToInstanceRoute_WithNumberAndText()
    {
        var (client, handler) = Build();

        await client.SendTextAsync("vendas", "5511999999999", "Nova venda!");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://evolution.local/message/sendText/vendas", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("5511999999999", handler.LastBody);
        Assert.Contains("Nova venda!", handler.LastBody);
    }

    // Resposta de erro da Evolution API (ex.: 401) vira HttpRequestException — falha não pode passar silenciosa.
    [Fact]
    public async Task SendTextAsync_WhenEvolutionReturnsError_Throws()
    {
        var (client, _) = Build(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendTextAsync("vendas", "5511999999999", "x"));
    }

    // Criar instância envia instanceName, número e integração Baileys no corpo.
    [Fact]
    public async Task CreateInstanceAsync_PostsInstancePayload()
    {
        var (client, handler) = Build();

        await client.CreateInstanceAsync("mv-5511999999999", "5511999999999");

        Assert.Equal("/instance/create", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("mv-5511999999999", handler.LastBody);
        Assert.Contains("WHATSAPP-BAILEYS", handler.LastBody);
    }

    // Configurar webhook envia url e lista de eventos aninhados no objeto webhook (formato v2).
    [Fact]
    public async Task SetWebhookAsync_PostsUrlAndEvents()
    {
        var (client, handler) = Build();

        await client.SetWebhookAsync("mv-1", "http://api/webhooks/evolution/s3cret", ["MESSAGES_UPSERT"]);

        Assert.Equal("/webhook/set/mv-1", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("http://api/webhooks/evolution/s3cret", handler.LastBody);
        Assert.Contains("MESSAGES_UPSERT", handler.LastBody);
    }

    // Connect devolve o QR code (code/base64/pairingCode) parseado da resposta.
    [Fact]
    public async Task ConnectAsync_ParsesQrCode()
    {
        var (client, _) = Build(body: """{"code":"QRDATA","base64":"data:image/png;base64,abc","pairingCode":"1234"}""");

        var qr = await client.ConnectAsync("mv-1");

        Assert.Equal("QRDATA", qr.Code);
        Assert.Equal("data:image/png;base64,abc", qr.Base64);
        Assert.Equal("1234", qr.PairingCode);
    }

    // ConnectionState lê o state dentro do objeto instance da resposta.
    [Fact]
    public async Task GetConnectionStateAsync_ParsesState()
    {
        var (client, _) = Build(body: """{"instance":{"instanceName":"mv-1","state":"open"}}""");

        var state = await client.GetConnectionStateAsync("mv-1");

        Assert.Equal("open", state);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public string Body { get; init; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(StatusCode) { Content = new StringContent(Body) };
        }
    }
}
