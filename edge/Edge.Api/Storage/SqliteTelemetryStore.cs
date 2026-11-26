using Edge.Api.Configuration;
using Edge.Api.Models;
using Edge.Api.Services;
using Microsoft.Data.Sqlite;

namespace Edge.Api.Storage;

public enum AcceptanceResult
{
    Created,
    Duplicate,
    Conflict,
    CapacityExceeded
}

public sealed class SqliteTelemetryStore : IDisposable
{
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly EdgeSettings settings;
    private readonly TelemetryValidator validator;
    private readonly string connectionString;

    internal SqliteTelemetryStore(EdgeSettings settings, TelemetryValidator validator)
    {
        this.settings = settings;
        this.validator = validator;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = settings.DatabasePath,
            DefaultTimeout = settings.WriteTimeoutSeconds,
            Pooling = false
        }.ToString();
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath)!);
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        if (command.ExecuteScalar() as string != "wal")
        {
            throw new InvalidOperationException("SQLite no pudo activar WAL.");
        }

        command.CommandText = "PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS telemetry_messages (
                node_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                payload TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('Pending', 'Sending', 'Delivered', 'Failed')),
                received_at INTEGER NOT NULL,
                attempt_count INTEGER NOT NULL,
                next_retry_at INTEGER NOT NULL,
                delivered_at INTEGER NULL,
                last_error TEXT NULL,
                PRIMARY KEY (node_id, seq)
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<AcceptanceResult> AcceptAsync(TelemetryMessage message, CancellationToken cancellationToken)
    {
        // SQLite has one writer. Waiting here avoids blocking a thread per request
        // and leaves SQLite's own timeout to handle writers in other processes.
        await writer.WaitAsync(cancellationToken);
        try
        {
            return Accept(message);
        }
        finally
        {
            writer.Release();
        }
    }

    public void Dispose() => writer.Dispose();

    private AcceptanceResult Accept(TelemetryMessage message)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();

        // Reserve the write lock before inspecting the key or counting rows.
        using var transaction = connection.BeginTransaction(deferred: false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload FROM telemetry_messages WHERE node_id = $node AND seq = $seq;
            """;
        command.Parameters.AddWithValue("$node", message.NodeId);
        command.Parameters.AddWithValue("$seq", message.Seq);

        if (command.ExecuteScalar() is string original)
        {
            var duplicate = validator.Validate(original).Content == message.Content;
            transaction.Commit();
            return duplicate ? AcceptanceResult.Duplicate : AcceptanceResult.Conflict;
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_messages;";
        if ((long)command.ExecuteScalar()! >= settings.Capacity)
        {
            transaction.Commit();
            return AcceptanceResult.CapacityExceeded;
        }

        var receivedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.CommandText = """
            INSERT INTO telemetry_messages (node_id, seq, payload, status, received_at, attempt_count, next_retry_at)
            VALUES ($node, $seq, $payload, $status, $receivedAt, 0, $receivedAt);
            """;
        command.Parameters.AddWithValue("$node", message.NodeId);
        command.Parameters.AddWithValue("$seq", message.Seq);
        command.Parameters.AddWithValue("$payload", message.Payload);
        command.Parameters.AddWithValue("$status", nameof(TelemetryStatus.Pending));
        command.Parameters.AddWithValue("$receivedAt", receivedAt);
        command.ExecuteNonQuery();
        transaction.Commit();
        return AcceptanceResult.Created;
    }
}
