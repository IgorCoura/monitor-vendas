using System.Net;
using MonitorVendas.Api.Common;
using MonitorVendas.Api.Integrations.Evolution;
using MonitorVendas.Tests.Infrastructure;

namespace MonitorVendas.Tests.Integrations;

public class EvolutionApiClientTests
{
    private static (EvolutionApiClient Client, RecordingHandler Handler) Build(HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var handler = new RecordingHandler { StatusCode = status, Body = body };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://evolution.local/") };
        return (new EvolutionApiClient(http, new FixedRandomSource()), handler);
    }

    // Enviar texto faz POST em message/sendText/{instance} com number, text e a
    // digitação simulada (presence composing + delay proporcional ao texto).
    [Fact]
    public async Task SendTextAsync_PostsToInstanceRoute_WithTypingSimulation()
    {
        var (client, handler) = Build(body: """{"key":{"id":"MSG-1"}}""");

        var result = await client.SendTextAsync("vendas", "5511999999999", "Nova venda!");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://evolution.local/message/sendText/vendas", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("5511999999999", handler.LastBody);
        Assert.Contains("Nova venda!", handler.LastBody);
        Assert.Contains("\"presence\":\"composing\"", handler.LastBody);
        Assert.Equal("MSG-1", result.KeyId);
        // O delay é sorteado: o teste garante a faixa, nunca o valor exato.
        Assert.InRange(result.DelayMs, HumanDelay.MinMs, HumanDelay.MaxMs);
        Assert.Contains($"\"delay\":{result.DelayMs}", handler.LastBody);
    }

    // Resposta de erro da Evolution API (ex.: 401) vira HttpRequestException — falha não pode passar silenciosa.
    [Fact]
    public async Task SendTextAsync_WhenEvolutionReturnsError_Throws()
    {
        var (client, _) = Build(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendTextAsync("vendas", "5511999999999", "x"));
    }

    // Criar instância envia instanceName, número e integração Baileys no corpo.
    // Sem proxy, nenhum campo `proxy*` vai junto.
    [Fact]
    public async Task CreateInstanceAsync_PostsInstancePayload()
    {
        var (client, handler) = Build();

        await client.CreateInstanceAsync("mv-5511999999999", "5511999999999", proxy: null);

        Assert.Equal("/instance/create", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("mv-5511999999999", handler.LastBody);
        Assert.Contains("WHATSAPP-BAILEYS", handler.LastBody);
        Assert.DoesNotContain("proxyHost", handler.LastBody);
    }

    // Com proxy, os campos vão PLANOS (não aninhados) e a porta como STRING —
    // é o contrato real da v2.3.7, e é o que faz o número nascer atrás do IP.
    [Fact]
    public async Task CreateInstanceAsync_WithProxy_SendsFlatFieldsAndStringPort()
    {
        var (client, handler) = Build();

        await client.CreateInstanceAsync("mv-1", phone: null,
            new ProxyCredentials("191.0.0.1", 8080, "socks5", "u", "p"));

        Assert.Contains("\"proxyHost\":\"191.0.0.1\"", handler.LastBody);
        Assert.Contains("\"proxyPort\":\"8080\"", handler.LastBody);
        Assert.Contains("\"proxyProtocol\":\"socks5\"", handler.LastBody);
    }

    // A Evolution testa o proxy antes de criar e aborta a instância inteira com
    // 400. Isso precisa chegar como InvalidProxyException, não como falha de
    // comunicação: é o que permite recriar sem proxy em vez de travar o operador.
    [Fact]
    public async Task CreateInstanceAsync_WhenProxyIsRejected_ThrowsInvalidProxy()
    {
        var (client, _) = Build(HttpStatusCode.BadRequest, """{"message":"Invalid proxy"}""");

        await Assert.ThrowsAsync<InvalidProxyException>(() =>
            client.CreateInstanceAsync("mv-1", null, new ProxyCredentials("1.1.1.1", 80, "http", null, null)));
    }

    // Trocar o proxy de uma instância existente é POST em proxy/set/{instance}.
    [Fact]
    public async Task SetProxyAsync_PostsToProxySetRoute()
    {
        var (client, handler) = Build();

        Assert.True(await client.SetProxyAsync("mv-1", new ProxyCredentials("191.0.0.1", 8080, "socks5", "u", "p")));

        Assert.Equal("/proxy/set/mv-1", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("\"enabled\":true", handler.LastBody);
        Assert.Contains("\"port\":\"8080\"", handler.LastBody);
    }

    // Remover o proxy é `enabled: false` — o schema da Evolution ainda exige
    // host/port/protocol não vazios, então vão valores de descarte.
    [Fact]
    public async Task SetProxyAsync_WithoutCredentials_DisablesTheProxy()
    {
        var (client, handler) = Build();

        Assert.True(await client.SetProxyAsync("mv-1", proxy: null));

        Assert.Contains("\"enabled\":false", handler.LastBody);
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

        Assert.Equal("open", state.State);
        Assert.False(state.Missing);
    }

    // Sem o objeto `instance` na resposta não há estado: devolver null é o que faz
    // a reconciliação não inventar uma mudança de status.
    [Fact]
    public async Task GetConnectionStateAsync_WithoutInstance_IsNull()
    {
        var (client, _) = Build(body: """{"error":"not found"}""");

        var state = await client.GetConnectionStateAsync("mv-1");

        Assert.Null(state.State);
        Assert.False(state.Missing);
    }

    // Regressão do número fantasma: o 404 de instância inexistente é RESPOSTA
    // (Missing), não exceção — tratado como "fora do ar", o número ficava
    // conectado para sempre no painel.
    [Fact]
    public async Task GetConnectionStateAsync_WhenInstanceDoesNotExist_ReportsMissing()
    {
        var (client, _) = Build(HttpStatusCode.NotFound,
            """{"status":404,"error":"Not Found","response":{"message":["The \"mv-1\" instance does not exist"]}}""");

        var state = await client.GetConnectionStateAsync("mv-1");

        Assert.True(state.Missing);
        Assert.Null(state.State);
    }

    // fetchInstances devolve nome e data de criação de cada instância — é a
    // matéria-prima da varredura de órfãs.
    [Fact]
    public async Task FetchInstancesAsync_ParsesNamesAndCreatedAt()
    {
        var (client, _) = Build(body:
            """[{"name":"mv-abc","createdAt":"2026-08-01T23:55:28.986Z"},{"name":"outro-app"},{"noname":true}]""");

        var instances = await client.FetchInstancesAsync();

        Assert.Equal(2, instances.Count);
        Assert.Equal("mv-abc", instances[0].Name);
        Assert.Equal(new DateTime(2026, 8, 1, 23, 55, 28, 986, DateTimeKind.Utc), instances[0].CreatedAt);
        Assert.Null(instances[1].CreatedAt);
    }

    // A Evolution devolve as mensagens ora como array, ora aninhadas em
    // `messages.records`, e o timestamp ora número, ora string. Formato não lido é
    // mensagem perdida na reconciliação.
    [Fact]
    public async Task FindMessagesAsync_ReadsNestedRecordsAndStringTimestamps()
    {
        var (client, _) = Build(body: """
            {
              "messages": {
                "records": [
                  {
                    "key": { "remoteJid": "5511922221111@s.whatsapp.net", "fromMe": false, "id": "N-1" },
                    "messageTimestamp": "1785000000"
                  }
                ]
              }
            }
            """);

        var found = await client.FindMessagesAsync("mv-1");

        var message = Assert.Single(found);
        Assert.Equal("N-1", message.KeyId);
        Assert.Equal(new DateTime(2026, 7, 25, 17, 20, 0, DateTimeKind.Utc), message.Timestamp);
    }

    // Mídia sem base64 na resposta não vira anexo: a análise segue pelo texto.
    [Fact]
    public async Task GetMediaAsync_WithoutBase64_IsNull()
    {
        var (client, _) = Build(body: """{"mimetype":"audio/ogg"}""");

        Assert.Null(await client.GetMediaAsync("mv-1", "m-1"));
    }

    // Sem `mimetype` o anexo ainda vale, com um tipo genérico — perder o áudio por
    // causa de um campo ausente seria pior.
    [Fact]
    public async Task GetMediaAsync_WithoutMimetype_FallsBackToOctetStream()
    {
        var (client, _) = Build(body: """{"base64":"AAAA"}""");

        var media = await client.GetMediaAsync("mv-1", "m-1");

        Assert.Equal("application/octet-stream", media!.MimeType);
    }

    // Erro HTTP ao buscar mídia também degrada para "sem áudio", nunca derruba a
    // análise da conversa.
    [Fact]
    public async Task GetMediaAsync_OnHttpError_IsNull()
    {
        var (client, _) = Build(HttpStatusCode.InternalServerError, "erro");

        Assert.Null(await client.GetMediaAsync("mv-1", "m-1"));
    }

    // Evolution fora do ar: as operações best-effort respondem "não deu" em vez de
    // estourar. Quem chama registra a decisão do operador de qualquer jeito.
    [Fact]
    public async Task WhenEvolutionIsUnreachable_BestEffortCallsReportFailure()
    {
        var client = new EvolutionApiClient(new HttpClient(new ThrowingHandler())
        {
            BaseAddress = new Uri("http://evolution.local/"),
        }, new FixedRandomSource());

        Assert.Null(await client.GetMediaAsync("mv-1", "m-1"));
        Assert.False(await client.LogoutAsync("mv-1"));
        Assert.False(await client.DeleteInstanceAsync("mv-1"));
    }

    // Erro HTTP (instância já derrubada, por exemplo) também é "não deu", não
    // exceção: derrubar sessão que já caiu não pode impedir o registro do ban.
    [Fact]
    public async Task LogoutAndDelete_OnHttpError_ReturnFalse()
    {
        var (client, _) = Build(HttpStatusCode.NotFound, "erro");

        Assert.False(await client.LogoutAsync("mv-1"));
        Assert.False(await client.DeleteInstanceAsync("mv-1"));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("conexão recusada");
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
