namespace MonitorVendas.Api.Integrations.Evolution;

// Credenciais que a Evolution precisa para pôr uma instância atrás de um proxy.
// Vive aqui, e não em Features/Proxies, para o cliente HTTP não depender da
// feature — é ela que depende dele.
public sealed record ProxyCredentials(string Host, int Port, string Protocol, string? Username, string? Password);

// A Evolution testa o proxy antes de criar a instância e aborta tudo com 400
// quando ele falha. Sem um tipo próprio, isso chegaria a quem chama como uma
// falha de comunicação qualquer e o pareamento morreria em vez de degradar.
public sealed class InvalidProxyException(string detail)
    : Exception($"A Evolution recusou o proxy: {detail}");
