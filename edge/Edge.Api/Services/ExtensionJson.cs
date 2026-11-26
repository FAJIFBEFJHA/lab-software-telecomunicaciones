using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Edge.Api.Services;

internal static class ExtensionJson
{
    private const int MaximumDepth = 8;
    private const int MaximumBytes = 4096;

    public static bool TryNormalize(JsonElement value, out string normalized)
    {
        normalized = "";
        var output = new StringBuilder();
        if (value.ValueKind != JsonValueKind.Object || !TryWrite(value, 0, output) ||
            output.Length > MaximumBytes)
        {
            return false;
        }

        // The result is ASCII, so the character count is also the byte count
        // that PHP measures with json_encode (including escaped Unicode and slashes).
        normalized = output.ToString();
        return true;
    }

    private static bool TryWrite(JsonElement value, int depth, StringBuilder output)
    {
        if (depth > MaximumDepth)
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    properties[property.Name] = property.Value;
                }

                output.Append('{');
                var first = true;
                foreach (var (name, child) in properties)
                {
                    if (!TelemetryValidator.IsIdentifier(name))
                    {
                        return false;
                    }

                    if (!first)
                    {
                        output.Append(',');
                    }
                    first = false;
                    WriteString(name, output);
                    output.Append(':');
                    if (!TryWrite(child, depth + 1, output))
                    {
                        return false;
                    }
                }
                output.Append('}');
                return true;

            case JsonValueKind.Array:
                output.Append('[');
                var separator = "";
                foreach (var item in value.EnumerateArray())
                {
                    output.Append(separator);
                    separator = ",";
                    if (!TryWrite(item, depth + 1, output))
                    {
                        return false;
                    }
                }
                output.Append(']');
                return true;

            case JsonValueKind.String:
                WriteString(value.GetString()!, output);
                return true;

            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer))
                {
                    output.Append(integer.ToString(CultureInfo.InvariantCulture));
                    return true;
                }
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    return false;
                }

                WriteFloat(number, output);
                return true;

            default:
                output.Append(value.GetRawText()); // null, true, false
                return true;
        }
    }

    private static void WriteFloat(double value, StringBuilder output)
    {
        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentPosition = shortest.IndexOf('E');
        if (exponentPosition < 0)
        {
            output.Append(shortest);
            return;
        }

        var mantissa = shortest.AsSpan(0, exponentPosition);
        var exponent = int.Parse(shortest.AsSpan(exponentPosition + 1), CultureInfo.InvariantCulture);
        output.Append(mantissa);
        if (!mantissa.Contains('.'))
        {
            output.Append(".0");
        }
        output.Append('e');
        if (exponent >= 0)
        {
            output.Append('+');
        }
        output.Append(exponent.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '/': output.Append("\\/"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                case < ' ' or > '\u007f':
                    output.Append("\\u");
                    output.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default: output.Append(character); break;
            }
        }
        output.Append('"');
    }
}
