namespace Edge.Api.Configuration;

internal sealed record EdgeSettings(string DatabasePath, int Capacity, int WriteTimeoutSeconds)
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

        return new EdgeSettings(Path.GetFullPath(path, contentRoot), capacity, timeout);
    }
}
