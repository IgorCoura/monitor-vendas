namespace MonitorVendas.Api.Features.Webhooks;

public sealed class WebhookOptions
{
    public const string Section = "Webhook";

    // Eventos que interessam ao monitor; tudo fora disso não é assinado na Evolution.
    public static readonly string[] SubscribedEvents =
    [
        "MESSAGES_UPSERT",
        "MESSAGES_UPDATE",
        "CONNECTION_UPDATE",
        "LABELS_EDIT",
        "LABELS_ASSOCIATION"
    ];

    public string Secret { get; set; } = string.Empty;
    public string PublicBaseUrl { get; set; } = string.Empty;
    public bool ProcessorEnabled { get; set; } = true;
    public int ProcessorIntervalSeconds { get; set; } = 2;

    public string CallbackUrl => $"{PublicBaseUrl.TrimEnd('/')}/api/v1/webhooks/evolution/{Secret}";
}
