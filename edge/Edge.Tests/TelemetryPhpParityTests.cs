using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Edge.Api.Services;

namespace Edge.Tests;

public sealed class TelemetryPhpParityTests
{
    [PhpFact]
    public async Task ComparesValidationAndNormalizedExtensionsWithHistoricalPhp()
    {
        var sample = EdgeTestHost.Sample;
        var inputs = new List<string>
        {
            sample,
            TelemetryEndpointTests.WithExtensions("{}"),
            sample.Replace("\"value\":1", "\"value\":1e0"),
            sample.Replace("\"seq\":1", "\"seq\":1.0"),
            sample.Replace("\"seq\":1", "\"seq\":1e0"),
            sample.Replace("\"timestamp\":null", "\"timestamp\":0.0"),
            sample.Replace("\"rssi\":null", "\"rssi\":-61.0"),
            sample.Replace("\"rssi\":null", "\"rssi\":-61"),
            sample.Replace("\"value\":1", "\"value\":true"),
            sample.Replace(",\"rssi\":null", ""),
            TelemetryEndpointTests.WithExtensions("null"),
            TelemetryEndpointTests.WithExtensions("[]"),
            TelemetryEndpointTests.WithExtensions("""{"z":[2,1.0],"a":{"2":0,"10":"café/☕"},"config_version":0,"interval_ms":5000}"""),
            TelemetryEndpointTests.WithExtensions("""{"k":1,"k":2}"""),
            TelemetryEndpointTests.WithExtensions("{\"k\":\"" + new string('é', 681) + "ab\"}"),
            TelemetryEndpointTests.WithExtensions("{\"k\":\"" + new string('é', 682) + "\"}"),
            TelemetryEndpointTests.WithExtensions("{\"k\":" + new string('[', 7) + "0" + new string(']', 7) + "}"),
            TelemetryEndpointTests.WithExtensions("{\"k\":" + new string('[', 8) + "0" + new string(']', 8) + "}"),
            "{"
        };
        foreach (var number in new[]
        {
            "-0", "-0.0", "1e-400", "1e400", "0.0001", "1e-5", "5e-324", "1e16", "1e17", "1.23e16",
            "9007199254740993", "9007199254740993.0", "9223372036854775807", "9223372036854775808",
            "1000000000000000100.0"
        })
        {
            inputs.Add(TelemetryEndpointTests.WithExtensions("{\"number\":" + number + "}"));
        }

        var reference = await ValidateWithPhpAsync(inputs);
        var validator = new TelemetryValidator();
        Assert.Equal(inputs.Count, reference.Length);
        for (var index = 0; index < inputs.Count; index++)
        {
            var expected = reference[index];
            var actual = ValidateWithEdge(validator, inputs[index]);
            var normalized = expected.Extensions is null ? null :
                Regex.Replace(expected.Extensions, @"[^\u0000-\u007f]", match =>
                    "\\u" + ((int)match.Value[0]).ToString("x4", CultureInfo.InvariantCulture));
            Assert.True(actual == new ValidationResult(expected.Status, normalized),
                $"Caso {index + 1}: PHP {expected.Status}/{normalized}; Edge {actual.Status}/{actual.Extensions}");
        }
    }

    private static ValidationResult ValidateWithEdge(TelemetryValidator validator, string input)
    {
        try
        {
            return new ValidationResult(200, validator.Validate(input).Content.Extensions);
        }
        catch (JsonException)
        {
            return new ValidationResult(400, null);
        }
        catch (TelemetryValidationException)
        {
            return new ValidationResult(422, null);
        }
    }

    private static async Task<ValidationResult[]> ValidateWithPhpAsync(List<string> inputs)
    {
        var bootstrap = Path.GetFullPath("../../../../../src/bootstrap.php", AppContext.BaseDirectory);
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("EDGE_PHP_EXECUTABLE")!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add("""
            require $argv[1];
            $results = [];
            foreach (json_decode(stream_get_contents(STDIN), false, 32, JSON_THROW_ON_ERROR) as $raw) {
                $extensions = null;
                try {
                    $payload = json_decode($raw, false, 32, JSON_THROW_ON_ERROR);
                    $status = 422;
                    if ($payload instanceof stdClass) {
                        $extensions = \Lab2\TelemetryService::validate($payload)['extensions'];
                        $status = 200;
                    }
                } catch (JsonException $error) {
                    $status = 400;
                } catch (\Lab2\ApiException $error) {
                    $status = $error->status;
                }
                $results[] = ['Status' => $status, 'Extensions' => $extensions];
            }
            echo json_encode($results, JSON_THROW_ON_ERROR);
            """);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(bootstrap);

        using var php = Process.Start(startInfo) ?? throw new InvalidOperationException("PHP no se pudo iniciar.");
        var output = php.StandardOutput.ReadToEndAsync();
        var errors = php.StandardError.ReadToEndAsync();
        await php.StandardInput.WriteAsync(JsonSerializer.Serialize(inputs));
        php.StandardInput.Close();
        await php.WaitForExitAsync();
        Assert.True(php.ExitCode == 0, await errors);
        return JsonSerializer.Deserialize<ValidationResult[]>(await output)!;
    }

    private sealed record ValidationResult(int Status, string? Extensions);

    private sealed class PhpFactAttribute : FactAttribute
    {
        public PhpFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDGE_PHP_EXECUTABLE")))
            {
                Skip = "Defina EDGE_PHP_EXECUTABLE para contrastar con el validador historico.";
            }
        }
    }
}
