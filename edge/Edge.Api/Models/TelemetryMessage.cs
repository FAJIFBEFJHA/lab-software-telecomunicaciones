namespace Edge.Api.Models;

public sealed record TelemetryMessage(string NodeId, long Seq, string Payload, TelemetryContent Content);

// Comparable content is separate from the original JSON retained in the queue.
public sealed record TelemetryContent(long? Timestamp, int Value, long? Rssi, string Extensions);

public enum TelemetryStatus
{
    Pending,
    Sending,
    Delivered,
    Failed
}
