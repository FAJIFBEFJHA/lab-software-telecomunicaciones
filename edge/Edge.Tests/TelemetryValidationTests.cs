using System.Net;
using System.Text.Json;

namespace Edge.Tests;

public sealed class TelemetryValidationTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("""{"a":1,"a":"\ud800"}""")]
    [InlineData("""{"\u0000a":1}""")]
    public async Task MalformedJsonReturns400(string json)
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var response = await host.PostAsync(json, EdgeTestHost.NodeToken);

        await TelemetryEndpointTests.AssertErrorAsync(response, HttpStatusCode.BadRequest, "malformed_json");
        Assert.Equal(0, host.Count());
    }

    [Theory]
    [InlineData("node_id")]
    [InlineData("seq")]
    [InlineData("timestamp")]
    [InlineData("value")]
    [InlineData("rssi")]
    public async Task MissingRequiredFieldReturns422WithDetails(string field)
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var sample = JsonDocument.Parse(EdgeTestHost.Sample);
        var fields = sample.RootElement.EnumerateObject()
            .Where(property => property.Name != field)
            .ToDictionary(property => property.Name, property => property.Value);
        using var response = await host.PostAsync(JsonSerializer.Serialize(fields), EdgeTestHost.NodeToken);

        await TelemetryEndpointTests.AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "validation_error");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("error").GetProperty("details").TryGetProperty(field, out _));
        Assert.Equal(0, host.Count());
    }

    [Theory]
    [InlineData("seq", "-1")]
    [InlineData("seq", "1.0")]
    [InlineData("seq", "1e0")]
    [InlineData("seq", "9007199254740992")]
    [InlineData("seq", "\"1\"")]
    [InlineData("timestamp", "1.0")]
    [InlineData("timestamp", "253402300800")]
    [InlineData("rssi", "-128")]
    [InlineData("rssi", "-1e0")]
    [InlineData("value", "true")]
    [InlineData("value", "\"1\"")]
    [InlineData("value", "2")]
    [InlineData("node_id", "\"invalid.node\"")]
    [InlineData("extensions", "null")]
    [InlineData("extensions", "[]")]
    [InlineData("extensions", """{"invalid.key":1}""")]
    [InlineData("extensions", """{"temp":1e400}""")]
    [InlineData("other", "0")]
    public async Task RejectsInvalidV1Field(string field, string jsonValue)
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var response = await host.PostAsync(WithField(field, jsonValue), EdgeTestHost.NodeToken);

        await TelemetryEndpointTests.AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "validation_error");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("error").GetProperty("details").TryGetProperty(field, out _));
        Assert.Equal(0, host.Count());
    }

    [Fact]
    public async Task AcceptsBoundsAndDoesNotRoundIntegersInExtensions()
    {
        await using var host = await EdgeTestHost.StartAsync();
        const string maximums = """
            {"node_id":"virtual-01","seq":9007199254740991,"timestamp":253402300799,"value":1.0,"rssi":-127,
              "extensions":{"integer":9007199254740993,"nested":{"underflow":1e-400}}}
            """;
        using var initial = await host.PostAsync(maximums, EdgeTestHost.NodeToken);
        using var duplicate = await host.PostAsync(maximums.Replace("1e-400", "0"), EdgeTestHost.NodeToken);
        using var conflict = await host.PostAsync(maximums.Replace("9007199254740993", "9007199254740993.0"),
            EdgeTestHost.NodeToken);

        Assert.Equal(HttpStatusCode.Created, initial.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        await TelemetryEndpointTests.AssertErrorAsync(conflict, HttpStatusCode.Conflict, "sequence_conflict");
        Assert.Equal(1, host.Count());
    }

    [Fact]
    public async Task EnforcesDepthAtBothPhpBoundaries()
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var atLimit = await host.PostAsync(
            TelemetryEndpointTests.WithExtensions(DeepExtension(7)), EdgeTestHost.NodeToken);
        using var extensionTooDeep = await host.PostAsync(
            TelemetryEndpointTests.WithExtensions(DeepExtension(8)), EdgeTestHost.NodeToken);
        using var invalidRoot = await host.PostAsync(new string('[', 31) + new string(']', 31), EdgeTestHost.NodeToken);
        using var tooDeepToParse = await host.PostAsync(new string('[', 32) + new string(']', 32), EdgeTestHost.NodeToken);

        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
        await TelemetryEndpointTests.AssertErrorAsync(extensionTooDeep, HttpStatusCode.UnprocessableEntity, "validation_error");
        await TelemetryEndpointTests.AssertErrorAsync(invalidRoot, HttpStatusCode.UnprocessableEntity, "validation_error");
        await TelemetryEndpointTests.AssertErrorAsync(tooDeepToParse, HttpStatusCode.BadRequest, "malformed_json");
    }

    [Theory]
    [InlineData('a', 4088, "")]
    [InlineData('/', 2044, "")]
    [InlineData('é', 681, "ab")]
    public async Task Enforces4096BytesUsingPhpJsonEscaping(char character, int count, string padding)
    {
        await using var host = await EdgeTestHost.StartAsync();
        var content = new string(character, count) + padding;
        using var atLimit = await host.PostAsync(TelemetryEndpointTests.WithExtensions("{\"k\":\"" + content + "\"}"),
            EdgeTestHost.NodeToken);
        using var tooLarge = await host.PostAsync(TelemetryEndpointTests.WithExtensions("{\"k\":\"" + content + "a\"}"),
            EdgeTestHost.NodeToken);

        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
        await TelemetryEndpointTests.AssertErrorAsync(tooLarge, HttpStatusCode.UnprocessableEntity, "validation_error");
    }

    private static string DeepExtension(int levels) =>
        "{\"k\":" + new string('[', levels) + "1" + new string(']', levels) + "}";

    private static string WithField(string field, string value)
    {
        using var sample = JsonDocument.Parse(EdgeTestHost.Sample);
        using var replacement = JsonDocument.Parse(value);
        var fields = sample.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        fields[field] = replacement.RootElement;
        return JsonSerializer.Serialize(fields);
    }
}
