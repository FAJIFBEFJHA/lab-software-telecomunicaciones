using System.Text.Json;
using Edge.Api.Models;

namespace Edge.Api.Services;

public sealed class TelemetryValidator
{
    public const int MaximumBodyBytes = 16_384;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private const long MaximumUnixSeconds = 253_402_300_799;

    public TelemetryMessage Validate(string payload)
    {
        // PHP json_decode(..., 32) accepts at most 31 open objects/arrays.
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 31 });
        var root = document.RootElement;
        CheckDecodedStrings(root);
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new TelemetryValidationException("El cuerpo debe ser un objeto JSON.");
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            fields[property.Name] = property.Value;
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields.Keys)
        {
            if (field is not ("node_id" or "seq" or "timestamp" or "value" or "rssi" or "extensions"))
            {
                errors[field] = "Campo desconocido; utilice extensions.";
            }
        }

        var node = fields.GetValueOrDefault("node_id");
        var nodeId = node.ValueKind == JsonValueKind.String ? node.GetString() : null;
        if (!IsIdentifier(nodeId))
        {
            errors["node_id"] = "Identificador invalido.";
        }

        var seq = fields.GetValueOrDefault("seq");
        if (!TryGetInteger(seq, 0, MaximumSafeInteger, out var sequence))
        {
            errors["seq"] = "Debe ser entero entre 0 y 9007199254740991.";
        }

        var value = fields.GetValueOrDefault("value");
        double numericValue = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out numericValue) ||
            numericValue is not (0 or 1))
        {
            errors["value"] = "PIR requiere el numero 0 o 1.";
        }

        var timestamp = NullableInteger("timestamp", 0, MaximumUnixSeconds);
        var rssi = NullableInteger("rssi", -127, 0);
        var extensions = "{}";
        if (fields.TryGetValue("extensions", out var extensionObject) &&
            !ExtensionJson.TryNormalize(extensionObject, out extensions))
        {
            errors["extensions"] = "Objeto JSON: maximo 4096 bytes, profundidad 8 y claves de 1 a 64 caracteres.";
        }

        if (errors.Count > 0)
        {
            throw new TelemetryValidationException("Telemetria invalida.", errors);
        }

        return new TelemetryMessage(nodeId!, sequence, payload,
            new TelemetryContent(timestamp, (int)numericValue, rssi, extensions));

        long? NullableInteger(string field, long minimum, long maximum)
        {
            if (!fields.TryGetValue(field, out var item))
            {
                errors[field] = "Campo obligatorio.";
            }
            else if (item.ValueKind != JsonValueKind.Null)
            {
                if (TryGetInteger(item, minimum, maximum, out var integer))
                {
                    return integer;
                }

                errors[field] = $"Debe ser entero entre {minimum} y {maximum}, o null.";
            }

            return null;
        }
    }

    internal static bool IsIdentifier(string? value) =>
        value is { Length: >= 1 and <= 64 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool TryGetInteger(JsonElement value, long minimum, long maximum, out long integer)
    {
        integer = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out integer) &&
            integer >= minimum && integer <= maximum;
    }

    private static void CheckDecodedStrings(JsonElement value)
    {
        // JsonDocument decodes names and strings lazily. PHP rejects malformed
        // Unicode even inside a property that a subsequent duplicate name replaces.
        try
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    _ = value.GetString();
                    break;
                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                    {
                        CheckDecodedStrings(item);
                    }
                    break;
                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                    {
                        if (property.Name.StartsWith('\0'))
                        {
                            throw new JsonException("Nombre de propiedad JSON invalido.");
                        }

                        CheckDecodedStrings(property.Value);
                    }
                    break;
            }
        }
        catch (InvalidOperationException error)
        {
            throw new JsonException("Cadena JSON invalida.", error);
        }
    }
}

public sealed class TelemetryValidationException(
    string message, IReadOnlyDictionary<string, string>? details = null) : Exception(message)
{
    public IReadOnlyDictionary<string, string>? Details { get; } = details;
}
