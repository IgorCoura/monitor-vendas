namespace MonitorVendas.Api.Integrations.Evolution;

public sealed class EvolutionOptions
{
    public const string Section = "Evolution";

    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}
