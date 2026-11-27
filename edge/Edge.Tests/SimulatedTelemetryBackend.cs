using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Edge.Tests;

internal sealed class SimulatedTelemetryBackend : IAsyncDisposable
{
    private readonly WebApplication app;
    private int requestCount;

    private SimulatedTelemetryBackend(WebApplication app) => this.app = app;

    public string Url => app.Urls.Single();
    public ConcurrentQueue<BackendRequest> Requests { get; } = new();

    public static async Task<int> Main(string[] args)
    {
        var port = 5090;
        var status = 201;
        if (args.Length > 2 ||
            (args.Length >= 1 && (!int.TryParse(args[0], out port) || port is < 1 or > 65_535)) ||
            (args.Length == 2 && (!int.TryParse(args[1], out status) ||
                status is not (200 or 201 or 400 or 401 or 403 or 408 or 409 or 422 or 429 or 503))))
        {
            Console.Error.WriteLine("Uso: [puerto 1–65535] [200|201|400|401|403|408|409|422|429|503]");
            return 2;
        }

        await using var simulator = await StartAsync((request, context) =>
        {
            if (!HttpMethods.IsPost(context.Request.Method) ||
                request.Path is not ("/api/v1/telemetry" or "/api/v1/telemetry/batch"))
            {
                return ReplyAsync(context, 404);
            }
            return status is 200 or 201 || (request.Path == "/api/v1/telemetry/batch" && status is 409 or 422)
                ? ConfirmAsync(request, context, status)
                : ReplyAsync(context, status);
        }, port, captureRequests: false);
        Console.WriteLine($"Backend simulado en {simulator.Url}, respuesta configurada {status}.");
        await simulator.app.WaitForShutdownAsync();
        return 0;
    }

    public static async Task<SimulatedTelemetryBackend> StartAsync(Func<BackendRequest, HttpContext, Task> reply,
        int port = 0, bool captureRequests = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [$"--urls=http://127.0.0.1:{port}"] });
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var simulator = new SimulatedTelemetryBackend(app);
        app.Run(async context =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                var request = new BackendRequest(Interlocked.Increment(ref simulator.requestCount),
                    context.Request.Path, context.Request.Headers.Authorization.ToString(), body,
                    context.Request.ContentLength);
                if (captureRequests)
                {
                    simulator.Requests.Enqueue(request);
                }
                await reply(request, context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // A timeout or orderly shutdown closes the simulated exchange.
            }
        });

        try
        {
            await app.StartAsync();
            return simulator;
        }
        catch
        {
            await simulator.DisposeAsync();
            throw;
        }
    }

    public static async Task ReplyAsync(HttpContext context, int status, string? body = null)
    {
        context.Response.StatusCode = status;
        if (body is not null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body, context.RequestAborted);
        }
    }

    public static Task ConfirmAsync(BackendRequest request, HttpContext context, int status = 201)
    {
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        if (request.Path == "/api/v1/telemetry/batch")
        {
            var messages = root.GetProperty("messages").EnumerateArray().ToArray();
            string node = messages[0].GetProperty("node_id").GetString()!;
            var results = messages.Select((message, index) => new
            {
                index,
                seq = message.GetProperty("seq").GetInt64(),
                status
            });
            return ReplyAsync(context, 200, Receipt(new { node_id = node, results }));
        }
        return ReplyAsync(context, status, Receipt(new
        {
            node_id = root.GetProperty("node_id").GetString(),
            seq = root.GetProperty("seq").GetInt64()
        }));
    }

    public static string Receipt(object data) => JsonSerializer.Serialize(new
    {
        ok = true,
        data,
        meta = new { api_version = "v1" }
    });

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

internal sealed record BackendRequest(int Number, string Path, string Authorization, string Body, long? BodyBytes);
