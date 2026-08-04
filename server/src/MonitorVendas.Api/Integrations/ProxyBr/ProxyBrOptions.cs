namespace MonitorVendas.Api.Integrations.ProxyBr;

public sealed class ProxyBrOptions
{
    public const string Section = "ProxyBr";

    // Desligado por default: sem token, sincronizar só produziria 401 em loop.
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://portal.proxybr.com.br/api/v1/";

    // Em dev vem do user-secrets; em Docker, de ProxyBr__Token. Se o portal
    // permitir emitir token somente-leitura, use — este token não precisa
    // comprar nem revogar nada.
    public string Token { get; set; } = string.Empty;

    public int SyncIntervalMinutes { get; set; } = 30;

    // O limite do fornecedor é 60/min POR CONTA (tokens diferentes dividem o
    // mesmo balde). 50 deixa folga para o operador clicar "testar" enquanto a
    // sincronização roda.
    public int RequestsPerMinute { get; set; } = 50;
}
