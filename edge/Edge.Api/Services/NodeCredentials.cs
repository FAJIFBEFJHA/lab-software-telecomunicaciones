using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Edge.Api.Services;

public sealed class NodeCredentials
{
    private readonly Dictionary<string, string> nodesByDigest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> tokensByNode = new(StringComparer.Ordinal);

    public NodeCredentials(IConfigurationSection nodes)
    {
        foreach (var node in nodes.GetChildren())
        {
            var token = node.Value;
            if (!TelemetryValidator.IsIdentifier(node.Key) || !IsToken(token) ||
                !nodesByDigest.TryAdd(Digest(token!), node.Key))
            {
                throw new InvalidOperationException(
                    "Edge:Nodes debe contener identidades validas con tokens hexadecimales unicos de 64 caracteres.");
            }

            tokensByNode.Add(node.Key, token!);
        }

        if (nodesByDigest.Count == 0)
        {
            throw new InvalidOperationException("Configure al menos un nodo en Edge:Nodes.");
        }
    }

    public string? Authenticate(string? authorization)
    {
        if (!AuthenticationHeaderValue.TryParse(authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            !IsToken(header.Parameter))
        {
            return null;
        }

        return nodesByDigest.GetValueOrDefault(Digest(header.Parameter!));
    }

    internal string? GetDeliveryToken(string nodeId) => tokensByNode.GetValueOrDefault(nodeId);

    private static bool IsToken(string? value) =>
        value is { Length: 64 } && value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static string Digest(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
