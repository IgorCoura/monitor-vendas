using Microsoft.Extensions.Options;
using MonitorVendas.Api.Integrations.Ai.Gemini;

namespace MonitorVendas.Api.Integrations.Ai;

public static class AiSetup
{
    public static IServiceCollection AddAiProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.Section));
        services.AddSingleton<AiCostCalculator>();

        services.AddHttpClient<GeminiProvider>((provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<AiOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds));
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                http.DefaultRequestHeaders.Add("x-goog-api-key", options.ApiKey);
        });

        // A troca de LLM acontece aqui: um IAiProvider novo e a chave Ai:Provider.
        // Transiente de propósito — segurar um typed client em singleton congela o
        // handler e o DNS junto com ele.
        services.AddTransient<IAiProvider>(provider =>
        {
            var name = provider.GetRequiredService<IOptions<AiOptions>>().Value.Provider;
            return name.ToLowerInvariant() switch
            {
                "gemini" => provider.GetRequiredService<GeminiProvider>(),
                _ => throw new InvalidOperationException($"Provedor de IA desconhecido em Ai:Provider: '{name}'."),
            };
        });

        return services;
    }
}
