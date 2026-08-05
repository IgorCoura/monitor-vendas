using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MonitorVendas.Api.Common;

namespace MonitorVendas.Api.Integrations.Evolution;

public static class EvolutionSetup
{
    public static IServiceCollection AddEvolutionApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EvolutionOptions>(configuration.GetSection(EvolutionOptions.Section));

        // O ruído do delay de digitação; os testes substituem por uma fonte fixa.
        services.TryAddSingleton<IRandomSource, RandomSource>();

        services.AddHttpClient<EvolutionApiClient>((provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<EvolutionOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl);
            http.DefaultRequestHeaders.Add("apikey", options.ApiKey);
        });

        return services;
    }
}
