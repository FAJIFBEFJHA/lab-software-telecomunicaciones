using System.Net;
using System.Text;
using System.Text.Json;

namespace Edge.Tests;

public sealed class TelemetryEndpointTests
{
    [Fact]
    public async Task NewMessageIsAcknowledgedAfterPersistenceAndPreservesExactJson()
    {
        await using var host = await EdgeTestHost.StartAsync();
        const string message = """
             { "value": 1.0, "rssi": null, "node_id": "virtual-01", "timestamp": null,
               "seq": 1, "extensions": {"pir_source":"virtual","config_version":0,"interval_ms":5000,"label":"café/☕"} }
            """;
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var response = await host.PostAsync(message, EdgeTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("v1", body.RootElement.GetProperty("meta").GetProperty("api_version").GetString());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal("virtual-01", data.GetProperty("node_id").GetString());
        Assert.Equal(1, data.GetProperty("seq").GetInt64());
        Assert.Equal("queued", data.GetProperty("state").GetString());

        using var connection = host.OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload, status, received_at, attempt_count, next_retry_at, delivered_at, last_error
            FROM telemetry_messages;
            """;
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(message, row.GetString(0));
        Assert.Equal("Pending", row.GetString(1));
        Assert.InRange(row.GetInt64(2), before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(0, row.GetInt64(3));
        Assert.Equal(row.GetInt64(2), row.GetInt64(4));
        Assert.True(row.IsDBNull(5));
        Assert.True(row.IsDBNull(6));
        Assert.False(row.Read());
    }

    [Theory]
    [InlineData("""{"node_id":"virtual-01","seq":1,"timestamp":null,"value":1,"rssi":null}""")]
    [InlineData("""{"extensions":{},"node_id":"virtual-01","seq":1,"timestamp":null,"value":1e0,"rssi":null}""")]
    [InlineData("""{"rssi":null,"timestamp":null,"value":1,"seq":1,"node_id":"virtual-01"}""")]
    public async Task EquivalentMessageReturns200(string retry)
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var created = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
        using var repeated = await host.PostAsync(retry, EdgeTestHost.NodeToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(1, host.Count());
    }

    [Fact]
    public async Task ComparesNestedObjectKeysAndNumbersButPreservesArrayOrder()
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var first = await host.PostAsync(WithExtensions("""{"list":[1,2],"object":{"10":2,"2":1}}"""),
            EdgeTestHost.NodeToken);
        using var equivalent = await host.PostAsync(WithExtensions("""{"object":{"2":1.0,"10":2e0},"list":[1e0,2]}"""),
            EdgeTestHost.NodeToken);
        using var changed = await host.PostAsync(WithExtensions("""{"list":[2,1],"object":{"10":2,"2":1}}"""),
            EdgeTestHost.NodeToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, equivalent.StatusCode);
        await AssertErrorAsync(changed, HttpStatusCode.Conflict, "sequence_conflict");
        Assert.Equal(1, host.Count());
    }

    [Fact]
    public async Task ReusedSequenceWithDifferentValueConflicts()
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var first = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
        using var conflict = await host.PostAsync(EdgeTestHost.Sample.Replace("\"value\":1", "\"value\":0"),
            EdgeTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertErrorAsync(conflict, HttpStatusCode.Conflict, "sequence_conflict");
        Assert.Equal(1, host.Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrecto")]
    public async Task UnknownTokenDoesNotReachValidation(string? token)
    {
        await using var host = await EdgeTestHost.StartAsync();
        var candidate = token is null ? null : new string('c', 64);
        using var response = await host.PostAsync("{", candidate);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Equal(0, host.Count());
    }

    [Fact]
    public async Task ValidTokenCannotAttributeTelemetryToAnotherNode()
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var response = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.OtherToken);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "identity_mismatch");
        Assert.Equal(0, host.Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnforcesBodyLimitForFixedLengthAndChunkedRequests(bool chunked)
    {
        await using var host = await EdgeTestHost.StartAsync();
        var maximum = EdgeTestHost.Sample.PadRight(16_384);
        using var atLimit = await host.PostAsync(maximum, EdgeTestHost.NodeToken, chunked);
        using var tooLarge = await host.PostAsync(maximum + " ", EdgeTestHost.NodeToken, chunked);

        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
        await AssertErrorAsync(tooLarge, HttpStatusCode.UnprocessableEntity, "validation_error");
        Assert.Equal(1, host.Count());
    }

    [Fact]
    public async Task RejectsNonJsonContentType()
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var response = await host.PostAsync(new StringContent(EdgeTestHost.Sample, Encoding.UTF8, "text/plain"),
            EdgeTestHost.NodeToken);

        await AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        Assert.Equal(0, host.Count());
    }

    [Fact]
    public async Task InvalidUtf8Returns400()
    {
        await using var host = await EdgeTestHost.StartAsync();
        var invalidBytes = new ByteArrayContent([0x22, 0xC3, 0x28, 0x22]);
        invalidBytes.Headers.ContentType = new("application/json");
        using var response = await host.PostAsync(invalidBytes, EdgeTestHost.NodeToken);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "malformed_json");
        Assert.Equal(0, host.Count());
    }

    internal static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var payload = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {status}, received {response.StatusCode}: {payload}");
        using var body = JsonDocument.Parse(payload);
        Assert.False(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(code, body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("v1", body.RootElement.GetProperty("meta").GetProperty("api_version").GetString());
    }

    internal static string WithExtensions(string extension) =>
        EdgeTestHost.Sample[..^1] + ",\"extensions\":" + extension + "}";
}
