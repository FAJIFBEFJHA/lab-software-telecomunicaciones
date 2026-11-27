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
    private readonly TimeProvider clock;
    private readonly string connectionString;

    internal SqliteTelemetryStore(EdgeSettings settings, TelemetryValidator validator, TimeProvider clock)
    {
        this.settings = settings;
        this.validator = validator;
        this.clock = clock;
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
            CREATE INDEX IF NOT EXISTS telemetry_pending_by_node
                ON telemetry_messages (node_id, next_retry_at, received_at, seq) WHERE status = 'Pending';
            """;
        command.ExecuteNonQuery();
    }

    public Task<AcceptanceResult> AcceptAsync(TelemetryMessage message, CancellationToken cancellationToken) =>
        WriteAsync(command => Accept(command, message), cancellationToken);

    internal Task<int> RecoverSendingAsync(CancellationToken cancellationToken) => WriteAsync(command =>
    {
        command.CommandText = """
            UPDATE telemetry_messages
            SET status = 'Pending', next_retry_at = $now, last_error = 'interrupted'
            WHERE status = 'Sending';
            """;
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery();
    }, cancellationToken);

    internal Task<(List<DeliveryAttempt> Attempts, long? BatchDeadline)> ClaimAsync(
        string previousNode, CancellationToken cancellationToken) =>
        WriteAsync(command => Claim(command, previousNode), cancellationToken);

    internal Task CompleteAsync(IReadOnlyList<DeliveryUpdate> updates, long completedAt,
        CancellationToken cancellationToken) => WriteAsync(command =>
    {
        command.CommandText = """
            UPDATE telemetry_messages
            SET status = $status, next_retry_at = COALESCE($nextRetryAt, next_retry_at),
                delivered_at = $deliveredAt, last_error = $error
            WHERE node_id = $node AND seq = $seq AND status = 'Sending' AND attempt_count = $attempt;
            """;
        foreach (var (attempt, result, nextRetryAt) in updates)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$node", attempt.NodeId);
            command.Parameters.AddWithValue("$seq", attempt.Seq);
            command.Parameters.AddWithValue("$attempt", attempt.AttemptCount);
            command.Parameters.AddWithValue("$status", result.Status.ToString());
            command.Parameters.AddWithValue("$nextRetryAt", (object?)nextRetryAt ?? DBNull.Value);
            command.Parameters.AddWithValue("$deliveredAt",
                result.Status == TelemetryStatus.Delivered ? completedAt : DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("El intento no coincide con la fila en envio.");
            }
        }
        return updates.Count;
    }, cancellationToken);

    public void Dispose() => writer.Dispose();

    private async Task<T> WriteAsync<T>(Func<SqliteCommand, T> write, CancellationToken cancellationToken)
    {
        // SQLite has one writer. Waiting here avoids blocking a thread per request
        // and leaves SQLite's own timeout to handle writers in other processes.
        await writer.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();

            using var transaction = connection.BeginTransaction(deferred: false);
            command.Transaction = transaction;
            var result = write(command);
            transaction.Commit();
            return result;
        }
        finally
        {
            writer.Release();
        }
    }

    private AcceptanceResult Accept(SqliteCommand command, TelemetryMessage message)
    {
        command.CommandText = """
            SELECT payload FROM telemetry_messages WHERE node_id = $node AND seq = $seq;
            """;
        command.Parameters.AddWithValue("$node", message.NodeId);
        command.Parameters.AddWithValue("$seq", message.Seq);

        if (command.ExecuteScalar() is string original)
        {
            var duplicate = validator.Validate(original).Content == message.Content;
            return duplicate ? AcceptanceResult.Duplicate : AcceptanceResult.Conflict;
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_messages;";
        if ((long)command.ExecuteScalar()! >= settings.Capacity)
        {
            return AcceptanceResult.CapacityExceeded;
        }

        var receivedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
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
        return AcceptanceResult.Created;
    }

    private (List<DeliveryAttempt> Attempts, long? BatchDeadline) Claim(SqliteCommand command, string previousNode)
    {
        var delivery = settings.Delivery;
        var batchSize = delivery.Mode == DeliveryMode.Batch ? delivery.BatchSize : 1;
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();

        // Advance through ready nodes in binary order, wrapping after the last one.
        // If no node is ready, report the earliest incomplete batch deadline.
        command.CommandText = """
            WITH eligible_nodes AS (
                SELECT node_id, MIN(next_retry_at) + $batchWait AS deadline, COUNT(*) AS sample_count
                FROM telemetry_messages
                WHERE status = 'Pending' AND next_retry_at <= $now
                GROUP BY node_id
            )
            SELECT node_id, deadline, sample_count >= $batchSize OR deadline <= $now AS ready
            FROM eligible_nodes
            ORDER BY ready DESC, CASE WHEN NOT ready THEN deadline END, node_id <= $previousNode, node_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$batchSize", batchSize);
        command.Parameters.AddWithValue("$batchWait", delivery.BatchWaitMilliseconds);
        command.Parameters.AddWithValue("$previousNode", previousNode);
        string node;
        using (var selected = command.ExecuteReader())
        {
            if (!selected.Read())
            {
                return ([], null);
            }
            if (!selected.GetBoolean(2))
            {
                return ([], selected.GetInt64(1));
            }
            node = selected.GetString(0);
        }

        command.Parameters.Clear();
        command.CommandText = """
            SELECT seq, payload, received_at, attempt_count FROM telemetry_messages
            WHERE node_id = $node AND status = 'Pending' AND next_retry_at <= $now
            ORDER BY next_retry_at, received_at, seq
            LIMIT $batchSize;
            """;
        command.Parameters.AddWithValue("$node", node);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$batchSize", batchSize);
        var attempts = new List<DeliveryAttempt>(batchSize);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                attempts.Add(new DeliveryAttempt(node, reader.GetInt64(0), reader.GetString(1),
                    reader.GetInt64(2), reader.GetInt64(3) + 1));
            }
        }

        command.Parameters.Clear();
        command.CommandText = """
            UPDATE telemetry_messages SET status = 'Sending', attempt_count = $attempt
            WHERE node_id = $node AND seq = $seq AND status = 'Pending';
            """;
        command.Parameters.AddWithValue("$node", node);
        var seq = command.Parameters.Add("$seq", SqliteType.Integer);
        var count = command.Parameters.Add("$attempt", SqliteType.Integer);
        foreach (var attempt in attempts)
        {
            seq.Value = attempt.Seq;
            count.Value = attempt.AttemptCount;
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("No se pudo registrar el intento en SQLite.");
            }
        }
        return (attempts, null);
    }
}
