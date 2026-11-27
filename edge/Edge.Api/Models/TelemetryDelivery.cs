namespace Edge.Api.Models;

internal sealed record DeliveryAttempt(string NodeId, long Seq, string Payload, long ReceivedAt, long AttemptCount);

internal sealed record DeliveryResult(TelemetryStatus Status, string? Error, long RetryNotBefore = 0);

internal sealed record DeliveryUpdate(DeliveryAttempt Attempt, DeliveryResult Result, long? NextRetryAt);
