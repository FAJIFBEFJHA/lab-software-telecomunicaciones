using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Edge.Api.Configuration;
using Edge.Api.Models;

namespace Edge.Api.Services;

internal sealed class BackendTelemetryClient(
    IHttpClientFactory clients, EdgeSettings settings, NodeCredentials credentials, TimeProvider clock,
    ILogger<BackendTelemetryClient> logger)
{
    internal const string ClientName = "TelemetryBackend";
    private const int MaximumReceiptBytes = 64 * 1024;
    private static readonly DeliveryResult Confirmed = new(TelemetryStatus.Delivered, null);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SocketsHttpHandler CreateHandler(DeliverySettings delivery)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = delivery.BackendUrl?.IsLoopback != true
        };
        if (delivery.BackendUrl?.Scheme == Uri.UriSchemeHttp)
        {
            handler.ConnectCallback = ConnectLoopbackAsync;
        }
        return handler;
    }

    public async Task<DeliveryResult[]> SendAsync(IReadOnlyList<DeliveryAttempt> attempts, CancellationToken stoppingToken)
    {
        var nodeId = attempts[0].NodeId;
        var token = credentials.GetDeliveryToken(nodeId);
        if (token is null)
        {
            return Repeat(new DeliveryResult(TelemetryStatus.Failed, "missing_node_credentials"));
        }

        var batch = settings.Delivery.Mode == DeliveryMode.Batch;
        var json = batch
            ? "{\"messages\":[" + string.Join(',', attempts.Select(attempt => attempt.Payload)) + "]}"
            : attempts[0].Payload;
        var body = Encoding.UTF8.GetBytes(json);
        // Intake bounds each original to 16 KiB. At most 10 such originals plus framing fit in 256 KiB.
        if (batch && body.Length > DeliverySettings.MaximumBatchBytes)
        {
            return Repeat(new DeliveryResult(TelemetryStatus.Failed, "batch_body_too_large"));
        }

        using var client = clients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            batch ? "/api/v1/telemetry/batch" : "/api/v1/telemetry");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var timeout = new CancellationTokenSource(settings.Delivery.Timeout, clock);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        var cancellation = requestCancellation.Token;

        logger.LogInformation("Solicitud de entrega {Mode} para {NodeId}: {SampleCount} muestras y {BodyBytes} bytes de cuerpo HTTP.",
            settings.Delivery.Mode, nodeId, attempts.Count, body.Length);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            var status = (int)response.StatusCode;
            var retryNotBefore = ReadRetryNotBefore(response.Headers);
            if (status != 200 && (batch || status != 201))
            {
                var result = IsTransient(status) || status is >= 200 and < 300
                    ? Retry($"http_{status}", retryNotBefore)
                    : new DeliveryResult(TelemetryStatus.Failed, $"http_{status}");
                return Repeat(result);
            }

            var invalid = Retry(batch ? "invalid_batch_receipt" : "invalid_receipt", retryNotBefore);
            if (response.Content.Headers.ContentLength > MaximumReceiptBytes)
            {
                return Repeat(invalid);
            }

            var buffer = new byte[MaximumReceiptBytes + 1];
            using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            int length = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellation);
            return length > MaximumReceiptBytes ? Repeat(invalid) : ReadReceipt(buffer.AsMemory(0, length), invalid);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return Repeat(Retry("timeout"));
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            stoppingToken.ThrowIfCancellationRequested();
            return Repeat(Retry("connection_error"));
        }

        DeliveryResult[] Repeat(DeliveryResult result) => Enumerable.Repeat(result, attempts.Count).ToArray();

        DeliveryResult[] ReadReceipt(ReadOnlyMemory<byte> bytes, DeliveryResult invalid)
        {
            try
            {
                _ = StrictUtf8.GetCharCount(bytes.Span);
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                var data = Property(root, "data");
                if (Property(root, "ok").ValueKind != JsonValueKind.True ||
                    !IsString(Property(Property(root, "meta"), "api_version"), "v1") ||
                    !IsString(Property(data, "node_id"), nodeId))
                {
                    return Repeat(invalid);
                }

                if (batch)
                {
                    return ReadBatchResults(Property(data, "results"), attempts, invalid);
                }
                return [IsInteger(Property(data, "seq"), out var seq) && seq == attempts[0].Seq ? Confirmed : invalid];
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException)
            {
                return Repeat(invalid);
            }
        }
    }

    private static DeliveryResult[] ReadBatchResults(JsonElement results, IReadOnlyList<DeliveryAttempt> attempts,
        DeliveryResult invalid)
    {
        var outcomes = Enumerable.Repeat(invalid, attempts.Count).ToArray();
        if (results.ValueKind != JsonValueKind.Array)
        {
            return outcomes;
        }

        var indexClaims = new int[attempts.Count];
        var sequenceClaims = new Dictionary<long, int>();
        var entries = new JsonElement[attempts.Count];
        foreach (var entry in results.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Count all claims, including those in malformed entries. An ambiguous claim
            // must not confirm a row even if one of the entries otherwise matches.
            foreach (var field in entry.EnumerateObject())
            {
                if (field.NameEquals("index") && IsInteger(field.Value, out var index) &&
                    index >= 0 && index < entries.Length)
                {
                    indexClaims[(int)index]++;
                    entries[(int)index] = entry;
                }
                if (field.NameEquals("seq") && IsInteger(field.Value, out var seq))
                {
                    sequenceClaims[seq] = sequenceClaims.GetValueOrDefault(seq) + 1;
                }
            }
        }

        for (int index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (indexClaims[index] != 1 || !IsInteger(Property(entry, "index"), out _) ||
                !IsInteger(Property(entry, "seq"), out var seq) || seq != attempts[index].Seq ||
                sequenceClaims[seq] != 1 || !IsInteger(Property(entry, "status"), out var status))
            {
                continue;
            }

            outcomes[index] = status switch
            {
                200 or 201 => Confirmed,
                409 or 422 => new DeliveryResult(TelemetryStatus.Failed, $"batch_http_{status}"),
                500 or 503 => Retry($"batch_http_{status}", invalid.RetryNotBefore),
                _ => invalid
            };
        }
        return outcomes;
    }

    private long ReadRetryNotBefore(HttpResponseHeaders headers)
    {
        var retry = headers.RetryAfter;
        if (retry?.Delta is { } delay)
        {
            return clock.GetUtcNow().ToUnixTimeMilliseconds() + Math.Max(0, (long)delay.TotalMilliseconds);
        }
        return retry?.Date?.ToUnixTimeMilliseconds() ?? 0;
    }

    private static bool IsTransient(int status) => status is 408 or 429 or >= 500 and <= 599;

    private static DeliveryResult Retry(string error, long retryNotBefore = 0) =>
        new(TelemetryStatus.Pending, error, retryNotBefore);

    private static async ValueTask<Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        IPAddress[] addresses;
        if (string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            // Avoid DNS or hosts-file overrides for cleartext connections.
            addresses = [IPAddress.IPv6Loopback, IPAddress.Loopback];
        }
        else if (IPAddress.TryParse(endpoint.Host, out var address) &&
            IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))
        {
            addresses = [address];
        }
        else
        {
            throw new HttpRequestException("HTTP de entrega requiere una direccion loopback.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsString(JsonElement field, string expected) =>
        field.ValueKind == JsonValueKind.String && field.ValueEquals(expected);

    private static bool IsInteger(JsonElement field, out long value)
    {
        value = 0;
        return field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out value);
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        JsonElement result = default;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name))
                {
                    if (result.ValueKind != JsonValueKind.Undefined)
                    {
                        return default;
                    }
                    result = property.Value;
                }
            }
        }
        return result;
    }
}
