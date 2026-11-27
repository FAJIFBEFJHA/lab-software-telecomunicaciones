using Edge.Api.Configuration;
using Edge.Api.Models;
using Edge.Api.Storage;

namespace Edge.Api.Services;

internal sealed class TelemetryDeliveryWorker(
    SqliteTelemetryStore store, BackendTelemetryClient backend, EdgeSettings settings, TimeProvider clock,
    ILogger<TelemetryDeliveryWorker> logger) : BackgroundService
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 32, 60];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recoveryNeeded = true;
        var previousNode = "";
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = settings.Delivery.PollInterval;
                try
                {
                    if (recoveryNeeded)
                    {
                        await RecoverAsync(stoppingToken);
                        recoveryNeeded = false;
                    }
                    if (!settings.Delivery.Enabled)
                    {
                        return;
                    }

                    var (attempts, batchDeadline) = await store.ClaimAsync(previousNode, stoppingToken);
                    if (attempts.Count > 0)
                    {
                        recoveryNeeded = true;
                        previousNode = attempts[0].NodeId;
                        var updates = await DeliverAsync(attempts, stoppingToken);
                        var completedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                        await store.CompleteAsync(updates, completedAt, stoppingToken);
                        recoveryNeeded = false;
                        LogResults(updates, completedAt);
                        continue;
                    }
                    if (batchDeadline is { } deadline)
                    {
                        var remaining = deadline - clock.GetUtcNow().ToUnixTimeMilliseconds();
                        delay = TimeSpan.FromMilliseconds(Math.Clamp(remaining, 1, (long)delay.TotalMilliseconds));
                    }
                }
                catch (Exception error) when (!(error is OperationCanceledException && stoppingToken.IsCancellationRequested))
                {
                    recoveryNeeded = true;
                    logger.LogWarning("Fallo inesperado de entrega ({FailureType}). Se reanudara la cola.",
                        error.GetType().Name);
                }

                await Task.Delay(delay, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown cancels HTTP and the polling delay.
        }
        finally
        {
            if (recoveryNeeded)
            {
                try
                {
                    await RecoverAsync(CancellationToken.None);
                }
                catch (Exception error)
                {
                    logger.LogWarning("No se pudo recuperar el envio al detener Edge ({FailureType}).",
                        error.GetType().Name);
                }
            }
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        int count = await store.RecoverSendingAsync(cancellationToken);
        if (count > 0)
        {
            logger.LogInformation("Se recuperaron {SampleCount} muestras con envio interrumpido.", count);
        }
    }

    private async Task<DeliveryUpdate[]> DeliverAsync(List<DeliveryAttempt> attempts, CancellationToken stoppingToken)
    {
        foreach (var attempt in attempts)
        {
            logger.LogInformation("Intento {AttemptCount} de entrega para {NodeId}, secuencia {Sequence}.",
                attempt.AttemptCount, attempt.NodeId, attempt.Seq);
        }

        var results = await backend.SendAsync(attempts, stoppingToken);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var updates = new DeliveryUpdate[attempts.Count];
        for (int index = 0; index < updates.Length; index++)
        {
            var attempt = attempts[index];
            var result = results[index];
            long? nextRetryAt = null;
            if (result.Status == TelemetryStatus.Pending)
            {
                int backoff = BackoffSeconds[(int)Math.Min(attempt.AttemptCount - 1, BackoffSeconds.Length - 1)] * 1000;
                int jitter = Random.Shared.Next(settings.Delivery.MaxJitterMilliseconds + 1);
                nextRetryAt = Math.Max(now + backoff + jitter, result.RetryNotBefore);
            }
            updates[index] = new DeliveryUpdate(attempt, result, nextRetryAt);
        }
        return updates;
    }

    private void LogResults(IEnumerable<DeliveryUpdate> updates, long completedAt)
    {
        foreach (var (attempt, result, nextRetryAt) in updates)
        {
            switch (result.Status)
            {
                case TelemetryStatus.Delivered:
                    logger.LogInformation(
                        "Entrega confirmada para {NodeId}, secuencia {Sequence}, intento {AttemptCount}, latencia {LatencyMilliseconds} ms.",
                        attempt.NodeId, attempt.Seq, attempt.AttemptCount, Math.Max(0, completedAt - attempt.ReceivedAt));
                    break;
                case TelemetryStatus.Failed:
                    logger.LogWarning("Fallo permanente para {NodeId}, secuencia {Sequence}, intento {AttemptCount}: {DeliveryError}.",
                        attempt.NodeId, attempt.Seq, attempt.AttemptCount, result.Error);
                    break;
                case TelemetryStatus.Pending:
                    logger.LogWarning(
                        "Reintento para {NodeId}, secuencia {Sequence}, tras intento {AttemptCount}: {DeliveryError}; proxima fecha Unix ms {NextRetryAt}.",
                        attempt.NodeId, attempt.Seq, attempt.AttemptCount, result.Error, nextRetryAt);
                    break;
            }
        }
    }
}
