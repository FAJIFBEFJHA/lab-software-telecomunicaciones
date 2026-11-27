namespace Edge.Api.Configuration;

internal sealed record EdgeSettings(
    string DatabasePath, int Capacity, int WriteTimeoutSeconds, DeliverySettings Delivery)
{
    public static EdgeSettings Load(IConfigurationSection settings, string contentRoot)
    {
        var path = settings["DatabasePath"] ?? ".runtime/edge.sqlite";
        var capacity = settings.GetValue("Capacity", 10_000);
        var timeout = settings.GetValue("WriteTimeoutSeconds", 3);

        if (string.IsNullOrWhiteSpace(path) || capacity < 1 || timeout < 1)
        {
            throw new InvalidOperationException(
                "Configure Edge:DatabasePath, Edge:Capacity y Edge:WriteTimeoutSeconds con valores validos.");
        }

        return new EdgeSettings(Path.GetFullPath(path, contentRoot), capacity, timeout,
            DeliverySettings.Load(settings.GetSection("Delivery")));
    }
}

internal enum DeliveryMode { Individual, Batch }

internal sealed record DeliverySettings(
    bool Enabled, Uri? BackendUrl, TimeSpan Timeout, TimeSpan PollInterval, DeliveryMode Mode,
    int BatchSize, int BatchWaitMilliseconds, int MaxJitterMilliseconds)
{
    public const int MaximumBatchSize = 10;
    public const int MaximumBatchBytes = 256 * 1024;

    public static DeliverySettings Load(IConfigurationSection settings)
    {
        var enabled = settings.GetValue("Enabled", false);
        var backend = settings["BackendUrl"];
        Uri? url = null;
        if ((enabled || !string.IsNullOrWhiteSpace(backend)) &&
            (!Uri.TryCreate(backend, UriKind.Absolute, out url) ||
             (url.Scheme != Uri.UriSchemeHttps && (url.Scheme != Uri.UriSchemeHttp || !url.IsLoopback)) ||
             url.Host.Length == 0 || url.UserInfo.Length != 0 || url.AbsolutePath != "/" ||
             url.Query.Length != 0 || url.Fragment.Length != 0))
        {
            throw new InvalidOperationException(
                "Edge:Delivery:BackendUrl requiere un origen HTTPS o HTTP de loopback.");
        }

        var timeout = settings.GetValue("TimeoutMilliseconds", 5000);
        var poll = settings.GetValue("PollMilliseconds", 500);
        var batchSize = settings.GetValue("BatchSize", MaximumBatchSize);
        var batchWait = settings.GetValue("BatchWaitMilliseconds", 2000);
        var jitter = settings.GetValue("MaxJitterMilliseconds", 250);
        if (timeout < 1 || poll < 1 || batchSize is < 1 or > MaximumBatchSize || batchWait < 0 || jitter is < 0 or > 60_000)
        {
            throw new InvalidOperationException("Configure valores validos en Edge:Delivery.");
        }

        var mode = (settings["Mode"] ?? "Individual").ToUpperInvariant() switch
        {
            "INDIVIDUAL" => DeliveryMode.Individual,
            "BATCH" => DeliveryMode.Batch,
            _ => throw new InvalidOperationException("Edge:Delivery:Mode debe ser Individual o Batch.")
        };

        return new DeliverySettings(enabled, url, TimeSpan.FromMilliseconds(timeout),
            TimeSpan.FromMilliseconds(poll), mode, batchSize, batchWait, jitter);
    }
}
