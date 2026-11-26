using System.Net;

namespace Edge.Tests;

public sealed class TelemetryStorageTests
{
    [Fact]
    public async Task SerializesConcurrentWritersOnTheSameSequence()
    {
        await using var host = await EdgeTestHost.StartAsync();
        var requests = Enumerable.Range(0, 12)
            .Select(index => host.PostAsync(index % 2 == 0
                ? EdgeTestHost.Sample
                : EdgeTestHost.Sample.Replace("\"value\":1", "\"value\":0"), EdgeTestHost.NodeToken));
        var responses = await Task.WhenAll(requests);
        try
        {
            var statuses = responses.Select(response => response.StatusCode).ToArray();
            Assert.Single(statuses, status => status == HttpStatusCode.Created);
            Assert.Equal(5, statuses.Count(status => status == HttpStatusCode.OK));
            Assert.Equal(6, statuses.Count(status => status == HttpStatusCode.Conflict));
            Assert.Equal(1, host.Count());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task FullQueueRejectsNewRowsButKeepsDeduplication()
    {
        await using var host = await EdgeTestHost.StartAsync(capacity: 1);
        using var initial = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
        using var full = await host.PostAsync(EdgeTestHost.Sample.Replace("\"seq\":1", "\"seq\":2"), EdgeTestHost.NodeToken);
        using var duplicate = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
        using var conflict = await host.PostAsync(EdgeTestHost.Sample.Replace("\"value\":1", "\"value\":0"),
            EdgeTestHost.NodeToken);

        Assert.Equal(HttpStatusCode.Created, initial.StatusCode);
        await TelemetryEndpointTests.AssertErrorAsync(full, HttpStatusCode.ServiceUnavailable, "queue_full");
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        await TelemetryEndpointTests.AssertErrorAsync(conflict, HttpStatusCode.Conflict, "sequence_conflict");
        Assert.Equal(1, host.Count());
    }

    [Fact]
    public async Task FailedSqliteInsertNeverReturnsAnAcknowledgement()
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var connection = host.OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_write BEFORE INSERT ON telemetry_messages
            BEGIN SELECT RAISE(ABORT, 'test failure'); END;
            """;
        command.ExecuteNonQuery();

        using var failed = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
        await TelemetryEndpointTests.AssertErrorAsync(failed, HttpStatusCode.InternalServerError, "storage_error");
        Assert.Equal(0, host.Count());

        command.CommandText = "DROP TRIGGER fail_write;";
        command.ExecuteNonQuery();
        using var retry = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task SQLiteLockReturns503AndAllowsRetry()
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var writer = host.OpenDatabase();
        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            using var locked = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
            await TelemetryEndpointTests.AssertErrorAsync(locked, HttpStatusCode.ServiceUnavailable, "storage_unavailable");
            Assert.Equal(0, host.Count());
        }

        using var retry = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task RestartPreservesOriginalAndDeduplication()
    {
        await using var host = await EdgeTestHost.StartAsync();
        using var initial = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.Created, initial.StatusCode);
        await host.RestartAsync();

        using var duplicate = await host.PostAsync(EdgeTestHost.Sample, EdgeTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        using var connection = host.OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar());
        command.CommandText = "SELECT payload FROM telemetry_messages;";
        Assert.Equal(EdgeTestHost.Sample, command.ExecuteScalar());
        Assert.Equal(1, host.Count());
    }
}
