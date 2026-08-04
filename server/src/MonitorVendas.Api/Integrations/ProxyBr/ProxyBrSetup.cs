using Microsoft.Extensions.Options;

namespace MonitorVendas.Api.Integrations.ProxyBr;

public static class ProxyBrSetup
{
    public static IServiceCollection AddProxyBr(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ProxyBrOptions>(configuration.GetSection(ProxyBrOptions.Section));

        services.AddHttpClient<ProxyBrClient>((provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<ProxyBrOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl);
            http.DefaultRequestHeaders.Authorization = new("Bearer", options.Token);
            // Sem isto, 401 e 429 podem voltar como HTML/redirect e o parser
            // quebra num lugar que não tem nada a ver com o problema real.
            http.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        return services;
    }
}
