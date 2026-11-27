using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Edge.Api.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Edge.Tests.SimulatedTelemetryBackend;

namespace Edge.Tests;

public sealed class TelemetryDeliveryTests
{
    private const string Node = "virtual-01";
    private const string OtherNode = "esp32-02";
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(8);

    [Theory]
    [InlineData(201)]
    [InlineData(200)]
    public async Task IndividualConfirmsAndPreservesTheOriginal(int status)
    {
        var clock = new ManualClock();
        var receivedAt = clock.Now;
        var canRespond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await StartAsync(async (request, context) =>
        {
            await canRespond.Task.WaitAsync(context.RequestAborted);
            await ConfirmAsync(request, context, status);
        });
        var logs = new TestLogs();
        await using var edge = await StartEdgeAsync(backend, clock, logs: logs);
        const string original = " \r\n  { \"seq\": 1, \"rssi\": null, \"node_id\":\"virtual-01\","
            + " \"timestamp\":null, \"value\":1e0, \"extensions\":{\"text\":\"café/☕\\u0061\"} } \r\n";
        await QueueAsync(edge, original: original);
        var sending = await WaitForRowAsync(edge, "Sending", attempts: 1);
        Assert.Equal(receivedAt, sending.ReceivedAt);
        Assert.Null(sending.DeliveredAt);

        clock.Advance(75);
        canRespond.SetResult();
        var delivered = await WaitForRowAsync(edge, "Delivered", attempts: 1);
        Assert.Equal(receivedAt, delivered.ReceivedAt);
        Assert.Equal(receivedAt + 75, delivered.DeliveredAt);
        Assert.Equal(original, delivered.Payload);
        Assert.Null(delivered.Error);
        var sent = Assert.Single(backend.Requests);
        Assert.Equal("/api/v1/telemetry", sent.Path);
        Assert.Equal("Bearer " + EdgeTestHost.NodeToken, sent.Authorization);
        Assert.Equal(original, sent.Body);
        Assert.Equal(Encoding.UTF8.GetByteCount(original), sent.BodyBytes);
        await WaitUntilAsync(() => logs.Messages.Any(message => message.Contains("latencia 75 ms")));
        Assert.Contains(logs.Messages, message => message.Contains($"{sent.BodyBytes} bytes de cuerpo HTTP"));
    }

    [Theory]
    [InlineData(200, "{")]
    [InlineData(201, "[]")]
    [InlineData(200, """{"ok":false,"data":{"node_id":"virtual-01","seq":1},"meta":{"api_version":"v1"}}""")]
    [InlineData(201, """{"ok":true,"data":{"node_id":"virtual-01","seq":1},"meta":{"api_version":"v2"}}""")]
    [InlineData(201, """{"ok":true,"data":{"node_id":"esp32-02","seq":1},"meta":{"api_version":"v1"}}""")]
    [InlineData(200, """{"ok":true,"data":{"node_id":"virtual-01","seq":2},"meta":{"api_version":"v1"}}""")]
    [InlineData(201, """{"ok":true,"data":{"node_id":"virtual-01","seq":1.0},"meta":{"api_version":"v1"}}""")]
    [InlineData(200, """{"ok":true,"data":{"node_id":"virtual-01","seq":2,"seq":1},"meta":{"api_version":"v1"}}""")]
    public async Task InvalidIndividualReceiptRemainsPendingAndRetries(int status, string invalid)
    {
        var clock = new ManualClock();
        await using var backend = await StartAsync((request, context) => request.Number == 1
            ? ReplyAsync(context, status, invalid) : ConfirmAsync(request, context));
        await using var edge = await StartEdgeAsync(backend, clock);
        await QueueAsync(edge);

        var pending = await WaitForRowAsync(edge, "Pending", attempts: 1);
        Assert.Equal("invalid_receipt", pending.Error);
        Assert.Null(pending.DeliveredAt);
        Assert.Equal(clock.Now + 1000, pending.NextRetryAt);
        clock.Advance(1000);
        await WaitForRowAsync(edge, "Delivered", attempts: 2);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task LocalhostCanReachAnIpv4LoopbackSimulator()
    {
        var clock = new ManualClock();
        await using var backend = await StartAsync((request, context) => ConfirmAsync(request, context));
        var configuration = DeliveryConfiguration(backend)
            .Select(argument => argument.Replace("127.0.0.1", "localhost", StringComparison.Ordinal))
            .ToArray();
        await using var edge = await EdgeTestHost.StartAsync(configuration: configuration,
            configureServices: services => services.AddSingleton<TimeProvider>(clock));
        await QueueAsync(edge);
        await WaitForRowAsync(edge, "Delivered", attempts: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconnectsAndResendsAfterLosingTheConnectionOrThePersistedReceipt(bool persistedBeforeAbort)
    {
        var clock = new ManualClock();
        var backendRows = new ConcurrentDictionary<(string, long), string>();
        await using var backend = await StartAsync((request, context) =>
        {
            var inserted = false;
            if (request.Number > 1 || persistedBeforeAbort)
            {
                inserted = backendRows.TryAdd((Node, 1), request.Body);
            }
            if (request.Number == 1)
            {
                context.Abort();
                return Task.CompletedTask;
            }
            return ConfirmAsync(request, context, inserted ? 201 : 200);
        });
        await using var edge = await StartEdgeAsync(backend, clock);
        var original = " " + EdgeTestHost.Sample + "\n";
        await QueueAsync(edge, original: original);

        var pending = await WaitForRowAsync(edge, "Pending", attempts: 1);
        Assert.Equal("connection_error", pending.Error);
        Assert.Equal(persistedBeforeAbort, backendRows.ContainsKey((Node, 1)));
        clock.Advance(1000);
        await WaitForRowAsync(edge, "Delivered", attempts: 2);
        Assert.Equal(original, Assert.Single(backendRows).Value);
        Assert.Equal(2, backend.Requests.Count);
        Assert.All(backend.Requests, request => Assert.Equal(original, request.Body));
    }

    [Fact]
    public async Task TimeoutIncludesReceiptBodyAnd503HonorsRetryAfter()
    {
        var clock = new ManualClock();
        await using var backend = await StartAsync(async (request, context) =>
        {
            if (request.Number == 1)
            {
                await context.Response.StartAsync(context.RequestAborted);
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            else if (request.Number == 2)
            {
                context.Response.Headers.RetryAfter = "7";
                await ReplyAsync(context, 503);
            }
            else
            {
                await ConfirmAsync(request, context);
            }
        });
        await using var edge = await StartEdgeAsync(backend, clock, timeoutMilliseconds: 500);
        await QueueAsync(edge);

        var timeout = await WaitForRowAsync(edge, "Pending", attempts: 1);
        Assert.Equal("timeout", timeout.Error);
        Assert.Equal(clock.Now + 1000, timeout.NextRetryAt);
        clock.Advance(1000);
        var unavailable = await WaitForRowAsync(edge, "Pending", attempts: 2);
        Assert.Equal("http_503", unavailable.Error);
        Assert.Equal(clock.Now + 7000, unavailable.NextRetryAt);
        clock.Advance(7000);
        await WaitForRowAsync(edge, "Delivered", attempts: 3);
    }

    [Fact]
    public async Task BackoffCapsAt60SecondsWithoutExhaustingAttempts()
    {
        var clock = new ManualClock();
        await using var backend = await StartAsync((request, context) => request.Number switch
        {
            1 => ReplyAsync(context, 408),
            <= 8 => ReplyAsync(context, 503),
            _ => ConfirmAsync(request, context)
        });
        await using var edge = await StartEdgeAsync(backend, clock);
        await QueueAsync(edge);

        var seconds = new[] { 1, 2, 4, 8, 16, 32, 60, 60 };
        for (int index = 0; index < seconds.Length; index++)
        {
            var pending = await WaitForRowAsync(edge, "Pending", attempts: index + 1);
            Assert.Equal(index == 0 ? "http_408" : "http_503", pending.Error);
            Assert.Equal(clock.Now + seconds[index] * 1000, pending.NextRetryAt);
            clock.Advance(seconds[index] * 1000);
        }
        await WaitForRowAsync(edge, "Delivered", attempts: 9);
    }

    [Fact]
    public async Task RetryAfterDateSetsALowerBoundAndJitterStaysWithinConfiguration()
    {
        var clock = new ManualClock();
        await using var backend = await StartAsync((request, context) =>
        {
            context.Response.Headers.RetryAfter = request.Number == 1
                ? "invalid" : clock.GetUtcNow().AddSeconds(8).ToString("R");
            return ReplyAsync(context, 429);
        });
        await using var edge = await StartEdgeAsync(backend, clock, maxJitterMilliseconds: 125);
        await QueueAsync(edge);
        var first = await WaitForRowAsync(edge, "Pending", attempts: 1);
        Assert.Equal("http_429", first.Error);
        Assert.InRange(first.NextRetryAt - clock.Now, 1000, 1125);
        // Advance to a whole second so the HTTP-date has an exact eight-second offset.
        clock.Advance(2000);
        var second = await WaitForRowAsync(edge, "Pending", attempts: 2);
        Assert.Equal(clock.Now + 8000, second.NextRetryAt);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    public async Task PermanentErrorsKeepTheRowAndDoNotRetry(int status)
    {
        var clock = new ManualClock();
        await using var backend = await StartAsync((request, context) => request.Number == 1
            ? ReplyAsync(context, status) : ConfirmAsync(request, context));
        await using var edge = await StartEdgeAsync(backend, clock, capacity: 2);
        await QueueAsync(edge);
        var failed = await WaitForRowAsync(edge, "Failed", attempts: 1);
        Assert.Equal($"http_{status}", failed.Error);
        Assert.Null(failed.DeliveredAt);
        Assert.Equal(EdgeTestHost.Sample, failed.Payload);

        clock.Advance(60_000);
        await QueueAsync(edge, seq: 2);
        await WaitForRowAsync(edge, "Delivered", seq: 2, attempts: 1);
        using var full = await edge.PostAsync(Sample(Node, 3), EdgeTestHost.NodeToken);
        using var duplicate = await edge.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(2, edge.Count());
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task BatchSendsTenMaximumSizeOriginalsWithoutReencodingSamples()
    {
        var clock = new ManualClock();
        await using var backend = await StartAsync((request, context) => ConfirmAsync(request, context));
        await using var edge = await StartEdgeAsync(backend, clock, batchSize: 10);
        var originals = Enumerable.Range(1, 10)
            .Select(seq => (" \n" + Sample(Node, seq)).PadRight(16_384)).ToArray();
        for (int index = 0; index < originals.Length; index++)
        {
            await QueueAsync(edge, seq: index + 1, original: originals[index]);
        }

        for (int index = 0; index < originals.Length; index++)
        {
            var row = await WaitForRowAsync(edge, "Delivered", seq: index + 1, attempts: 1);
            Assert.Equal(originals[index], row.Payload);
        }
        var request = Assert.Single(backend.Requests);
        var expected = "{\"messages\":[" + string.Join(',', originals) + "]}";
        Assert.Equal("/api/v1/telemetry/batch", request.Path);
        Assert.Equal("Bearer " + EdgeTestHost.NodeToken, request.Authorization);
        Assert.Equal(expected, request.Body);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), request.BodyBytes);
        Assert.InRange(request.BodyBytes!.Value, 10 * 16_384, 256 * 1024);
    }

    [Fact]
    public async Task BatchWaitsForItsOwnNodeThenSendsASingleOriginalAtDeadline()
    {
        var clock = new ManualClock();
        await using var backend = await StartAsync((request, context) => ConfirmAsync(request, context));
        await using var edge = await StartEdgeAsync(backend, clock, batchSize: 2);
        await QueueAsync(edge, Node);
        clock.Advance(1999);
        await QueueAsync(edge, OtherNode);
        await QueueAsync(edge, OtherNode, seq: 2);

        await WaitForRowAsync(edge, "Delivered", OtherNode, 2, attempts: 1);
        Assert.Equal("Pending", ReadRow(edge, Node, 1)!.Status);
        var fullBatch = Assert.Single(backend.Requests);
        Assert.Equal("Bearer " + EdgeTestHost.OtherToken, fullBatch.Authorization);
        Assert.Equal("{\"messages\":[" + Sample(OtherNode, 1) + "," + Sample(OtherNode, 2) + "]}", fullBatch.Body);

        clock.Advance(1);
        await WaitForRowAsync(edge, "Delivered", attempts: 1);
        var timedBatch = backend.Requests.OrderBy(request => request.Number).Last();
        Assert.Equal("Bearer " + EdgeTestHost.NodeToken, timedBatch.Authorization);
        Assert.Equal("{\"messages\":[" + Sample(Node, 1) + "]}", timedBatch.Body);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task BatchPersistsPartialResultsAndResendsOnlyTheUnconfirmedOriginals()
    {
        var clock = new ManualClock();
        const string results = """
            [{"index":4,"seq":5,"status":503},{"index":2,"seq":3,"status":409},
             {"index":0,"seq":1,"status":201},{"index":3,"seq":4,"status":422},{"index":1,"seq":2,"status":200}]
            """;
        await using var backend = await StartAsync((request, context) => request.Number == 1
            ? ReplyAsync(context, 200, BatchReceipt(results)) : ConfirmAsync(request, context, 200));
        await using var edge = await StartEdgeAsync(backend, clock, batchSize: 6);
        for (int seq = 1; seq <= 6; seq++)
        {
            await QueueAsync(edge, seq: seq);
        }

        await WaitForRowAsync(edge, "Delivered", seq: 1, attempts: 1);
        Assert.Equal("Delivered", ReadRow(edge, Node, 2)!.Status);
        Assert.Equal("batch_http_409", ReadRow(edge, Node, 3)!.Error);
        Assert.Equal("batch_http_422", ReadRow(edge, Node, 4)!.Error);
        Assert.Equal("Failed", ReadRow(edge, Node, 3)!.Status);
        Assert.Equal("Failed", ReadRow(edge, Node, 4)!.Status);
        Assert.Equal("batch_http_503", ReadRow(edge, Node, 5)!.Error);
        Assert.Equal("invalid_batch_receipt", ReadRow(edge, Node, 6)!.Error);
        Assert.Equal("Pending", ReadRow(edge, Node, 5)!.Status);
        Assert.Equal("Pending", ReadRow(edge, Node, 6)!.Status);
        Assert.Null(ReadRow(edge, Node, 6)!.DeliveredAt);
        Assert.Single(backend.Requests);

        clock.Advance(3000);
        await WaitForRowAsync(edge, "Delivered", seq: 5, attempts: 2);
        await WaitForRowAsync(edge, "Delivered", seq: 6, attempts: 2);
        var second = backend.Requests.OrderBy(request => request.Number).Last();
        Assert.Equal("{\"messages\":[" + Sample(Node, 5) + "," + Sample(Node, 6) + "]}", second.Body);
        Assert.Equal(2, backend.Requests.Count);
        Assert.Equal(6, edge.Count());
    }

    [Fact]
    public async Task BatchDoesNotConfirmAmbiguousIndicesOrSequences()
    {
        var clock = new ManualClock();
        const string results = """
            [{"index":0,"seq":1,"status":201},{"index":0,"seq":90,"status":409},
             {"index":1,"seq":2,"status":200},{"index":2,"seq":2,"status":201},
             {"index":3,"seq":44,"status":200},{"index":4,"seq":5,"status":201},
             {"index":80,"seq":81,"status":201},{"index":5,"index":"invalid","seq":6,"status":201},
             {"index":6,"seq":7,"status":202},{"index":7,"seq":8,"status":200},null]
            """;
        await using var backend = await StartAsync((request, context) => ReplyAsync(context, 200, BatchReceipt(results)));
        await using var edge = await StartEdgeAsync(backend, clock, batchSize: 8);
        for (int seq = 1; seq <= 8; seq++)
        {
            await QueueAsync(edge, seq: seq);
        }
        await WaitForRowAsync(edge, "Delivered", seq: 5, attempts: 1);
        Assert.Equal("Delivered", ReadRow(edge, Node, 8)!.Status);
        foreach (var seq in new[] { 1, 2, 3, 4, 6, 7 })
        {
            var row = ReadRow(edge, Node, seq)!;
            Assert.Equal("Pending", row.Status);
            Assert.Equal(1, row.AttemptCount);
            Assert.Equal("invalid_batch_receipt", row.Error);
            Assert.Null(row.DeliveredAt);
        }
    }

    [Theory]
    [InlineData(200, "esp32-02", "invalid_batch_receipt")]
    [InlineData(201, "virtual-01", "http_201")]
    public async Task BatchRequiresStatus200AndMatchingEnvelopeIdentity(int status, string node, string error)
    {
        var clock = new ManualClock();
        const string results = """[{"index":0,"seq":1,"status":201},{"index":1,"seq":2,"status":200}]""";
        await using var backend = await StartAsync((request, context) => ReplyAsync(context, status, BatchReceipt(results, node)));
        await using var edge = await StartEdgeAsync(backend, clock, batchSize: 2);
        await QueueAsync(edge);
        await QueueAsync(edge, seq: 2);

        foreach (int seq in new[] { 1, 2 })
        {
            var row = await WaitForRowAsync(edge, "Pending", seq: seq, attempts: 1);
            Assert.Equal(error, row.Error);
            Assert.Null(row.DeliveredAt);
        }
    }

    [Fact]
    public async Task StartupRecoversAnInterruptedAttemptFromTheExistingDatabase()
    {
        var clock = new ManualClock();
        await using var backend = await StartAsync((request, context) => ConfirmAsync(request, context, 200));
        await using var edge = await EdgeTestHost.StartAsync(configureServices: services => services.AddSingleton<TimeProvider>(clock));
        await QueueAsync(edge);
        var original = ReadRow(edge, Node, 1)!;
        // Model the committed claim left by an abruptly terminated process.
        ExecuteSql(edge, """
            UPDATE telemetry_messages SET status='Sending', attempt_count=1, next_retry_at=9223372036854775807;
            """);
        Assert.Equal("Sending", ReadRow(edge, Node, 1)!.Status);

        clock.Advance(5000);
        await edge.RestartAsync(DeliveryConfiguration(backend));
        var recovered = await WaitForRowAsync(edge, "Delivered", attempts: 2);
        Assert.Equal(original.ReceivedAt, recovered.ReceivedAt);
        Assert.Equal(original.Payload, Assert.Single(backend.Requests).Body);
        Assert.Equal(clock.Now, recovered.DeliveredAt);
        Assert.Null(recovered.Error);
    }

    [Fact]
    public async Task ShutdownCancelsHttpAndReturnsTheClaimToTheQueue()
    {
        var clock = new ManualClock();
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await StartAsync(async (request, context) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            finally
            {
                aborted.SetResult();
            }
        });
        await using var edge = await StartEdgeAsync(backend, clock, timeoutMilliseconds: 60_000);
        await QueueAsync(edge);
        await WaitUntilAsync(() => !backend.Requests.IsEmpty);
        await edge.RestartAsync([]).WaitAsync(TestDeadline);
        await aborted.Task.WaitAsync(TestDeadline);
        var recovered = await WaitForRowAsync(edge, "Pending", attempts: 1);
        Assert.Equal(clock.Now, recovered.NextRetryAt);
        Assert.Equal("interrupted", recovered.Error);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task ReceivesConcurrentWritesDuringBlockedHttpAndGivesAnotherNodeTheNextTurn()
    {
        var clock = new ManualClock();
        var canRespond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await StartAsync(async (request, context) =>
        {
            if (request.Number == 1)
            {
                await canRespond.Task.WaitAsync(context.RequestAborted);
            }
            if (request.Authorization == "Bearer " + EdgeTestHost.OtherToken)
            {
                await ReplyAsync(context, 503);
            }
            else
            {
                await ConfirmAsync(request, context);
            }
        });
        await using var edge = await StartEdgeAsync(backend, clock);
        await QueueAsync(edge, OtherNode);
        await WaitUntilAsync(() => backend.Requests.Count == 1);
        var arrivals = Enumerable.Range(2, 5).Select(seq => QueueAsync(edge, OtherNode, seq))
            .Append(QueueAsync(edge, Node, 2));
        await Task.WhenAll(arrivals).WaitAsync(TestDeadline);
        Assert.Equal(7, edge.Count());
        Assert.Single(backend.Requests);

        canRespond.SetResult();
        await WaitForRowAsync(edge, "Delivered", seq: 2, attempts: 1);
        Assert.Equal("Bearer " + EdgeTestHost.NodeToken, backend.Requests.OrderBy(request => request.Number).ElementAt(1).Authorization);
        Assert.Equal("http_503", ReadRow(edge, OtherNode, 1)!.Error);
    }

    [Fact]
    public async Task WorkerRecoversAfterAnUnexpectedSqliteCompletionFailure()
    {
        var clock = new ManualClock();
        var firstReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await StartAsync(async (request, context) =>
        {
            var gate = request.Number == 1 ? firstReply : secondReply;
            await gate.Task.WaitAsync(context.RequestAborted);
            await ConfirmAsync(request, context, request.Number == 1 ? 201 : 200);
        });
        await using var edge = await StartEdgeAsync(backend, clock);
        await QueueAsync(edge);
        await WaitUntilAsync(() => backend.Requests.Count == 1);
        ExecuteSql(edge, """
            CREATE TRIGGER reject_confirmation BEFORE UPDATE ON telemetry_messages
            WHEN NEW.status = 'Delivered'
            BEGIN SELECT RAISE(ABORT, 'test failure'); END;
            """);
        firstReply.SetResult();

        await WaitUntilAsync(() => backend.Requests.Count == 2);
        var retried = await WaitForRowAsync(edge, "Sending", attempts: 2);
        Assert.Equal("interrupted", retried.Error);
        ExecuteSql(edge, "DROP TRIGGER reject_confirmation;");
        secondReply.SetResult();
        await WaitForRowAsync(edge, "Delivered", attempts: 2);
    }

    [Fact]
    public async Task IgnoresRedirectDestinationAndKeepsCredentialsOutOfLogsAndSqlite()
    {
        var clock = new ManualClock();
        await using var destination = await StartAsync((request, context) => ConfirmAsync(request, context));
        await using var backend = await StartAsync((request, context) =>
        {
            context.Response.Headers.Location = destination.Url + "/api/v1/telemetry";
            return ReplyAsync(context, 307, request.Authorization);
        });
        var logs = new TestLogs();
        await using var edge = await StartEdgeAsync(backend, clock, logs: logs);
        await QueueAsync(edge);
        var failed = await WaitForRowAsync(edge, "Failed", attempts: 1);
        Assert.Equal("http_307", failed.Error);
        Assert.Empty(destination.Requests);
        Assert.Equal("Bearer " + EdgeTestHost.NodeToken, Assert.Single(backend.Requests).Authorization);
        await WaitUntilAsync(() => logs.Messages.Any(message => message.Contains("http_307")));
        var log = string.Join('\n', logs.Messages);
        using (var connection = edge.OpenDatabase())
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        using var file = new FileStream(edge.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var contents = new MemoryStream();
        await file.CopyToAsync(contents);
        var database = Encoding.UTF8.GetString(contents.ToArray());
        foreach (var token in new[] { EdgeTestHost.NodeToken, EdgeTestHost.OtherToken })
        {
            Assert.DoesNotContain(token, log);
            Assert.DoesNotContain(token, database);
        }
    }

    [Theory]
    [InlineData("http://192.0.2.1", false)]
    [InlineData("ftp://localhost", false)]
    [InlineData("https://secret@example.test", false)]
    [InlineData("https://example.test/api", false)]
    [InlineData("https://example.test/?secret=x", false)]
    [InlineData("https://example.test/", true)]
    [InlineData("http://127.0.0.1:9000", true)]
    [InlineData("http://[::1]:9000", true)]
    [InlineData("http://localhost:9000", true)]
    public void ValidatesTheBackendOrigin(string url, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delivery:Enabled"] = "true",
            ["Delivery:BackendUrl"] = url
        }).Build();
        var section = configuration.GetSection("Delivery");
        if (valid)
        {
            Assert.Equal(new Uri(url), DeliverySettings.Load(section).BackendUrl);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => DeliverySettings.Load(section));
        }
    }

    private static string[] DeliveryConfiguration(SimulatedTelemetryBackend backend, int timeoutMilliseconds = 5000,
        int maxJitterMilliseconds = 0, int? batchSize = null, int batchWaitMilliseconds = 2000) =>
    [
        "--Edge:Delivery:Enabled=true", $"--Edge:Delivery:BackendUrl={backend.Url}",
        "--Edge:Delivery:PollMilliseconds=10", $"--Edge:Delivery:TimeoutMilliseconds={timeoutMilliseconds}",
        $"--Edge:Delivery:MaxJitterMilliseconds={maxJitterMilliseconds}",
        $"--Edge:Delivery:Mode={(batchSize.HasValue ? "Batch" : "Individual")}",
        $"--Edge:Delivery:BatchSize={batchSize ?? 10}", $"--Edge:Delivery:BatchWaitMilliseconds={batchWaitMilliseconds}"
    ];

    private static Task<EdgeTestHost> StartEdgeAsync(SimulatedTelemetryBackend backend, ManualClock clock,
        int timeoutMilliseconds = 5000, int maxJitterMilliseconds = 0, int? batchSize = null,
        int batchWaitMilliseconds = 2000, int capacity = 100, TestLogs? logs = null) =>
        EdgeTestHost.StartAsync(capacity,
            [
                .. DeliveryConfiguration(backend, timeoutMilliseconds, maxJitterMilliseconds, batchSize, batchWaitMilliseconds),
                .. logs is null ? Array.Empty<string>() : ["--Logging:LogLevel:Default=Trace"]
            ],
            services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                if (logs is not null)
                {
                    services.AddLogging(logging => logging.AddProvider(logs));
                }
            });

    private static string Sample(string node, long seq) =>
        $$"""{"node_id":"{{node}}","seq":{{seq}},"timestamp":null,"value":1,"rssi":null}""";

    private static string BatchReceipt(string results, string node = Node) =>
        $$$"""{"ok":true,"data":{"node_id":"{{{node}}}","results":{{{results}}}},"meta":{"api_version":"v1"}}""";

    private static async Task QueueAsync(EdgeTestHost edge, string node = Node, long seq = 1, string? original = null)
    {
        var token = node == Node ? EdgeTestHost.NodeToken : EdgeTestHost.OtherToken;
        using var response = await edge.PostAsync(original ?? Sample(node, seq), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static void ExecuteSql(EdgeTestHost edge, string sql)
    {
        using var connection = edge.OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static Row? ReadRow(EdgeTestHost edge, string node, long seq)
    {
        using var connection = edge.OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, attempt_count, next_retry_at, received_at, delivered_at, payload, last_error
            FROM telemetry_messages WHERE node_id=$node AND seq=$seq;
            """;
        command.Parameters.AddWithValue("$node", node);
        command.Parameters.AddWithValue("$seq", seq);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new Row(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6)) : null;
    }

    private static async Task<Row> WaitForRowAsync(EdgeTestHost edge, string status, string node = Node, long seq = 1,
        long attempts = 0)
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        Row? row;
        do
        {
            row = ReadRow(edge, node, seq);
            if (row is not null && row.Status == status && row.AttemptCount == attempts)
            {
                return row;
            }
            await Task.Delay(10);
        } while (!deadline.IsCancellationRequested);
        Assert.Fail($"Expected {node}/{seq}: {status} after {attempts} attempts; last row was {row}.");
        return null!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        while (!condition() && !deadline.IsCancellationRequested)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "The expected condition was not observed before the test deadline.");
    }

    private sealed record Row(string Status, long AttemptCount, long NextRetryAt, long ReceivedAt,
        long? DeliveredAt, string Payload, string? Error);

    private sealed class ManualClock : TimeProvider
    {
        private long now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        public long Now => Interlocked.Read(ref now);
        public void Advance(long milliseconds) => Interlocked.Add(ref now, milliseconds);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    private sealed class TestLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception) + exception);
        public void Dispose() { }
    }
}
