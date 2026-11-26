namespace Edge.Api.Models;

public sealed record ApiMeta(string ApiVersion = "v1");

public sealed record TelemetryReceipt(string NodeId, long Seq, string State = "queued");

public sealed record TelemetrySuccess(TelemetryReceipt Data)
{
    public bool Ok => true;
    public ApiMeta Meta { get; } = new();
}

public sealed record ApiError(string Code, string Message, IReadOnlyDictionary<string, string> Details);

public sealed record TelemetryFailure(ApiError Error)
{
    public bool Ok => false;
    public ApiMeta Meta { get; } = new();
}
