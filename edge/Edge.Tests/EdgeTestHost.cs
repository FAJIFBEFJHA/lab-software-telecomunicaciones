using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Edge.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Edge.Tests;

internal sealed class EdgeTestHost : IAsyncDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "isolated-db", Guid.NewGuid().ToString("N"));
    private readonly int capacity;
    private readonly Action<IServiceCollection>? configureServices;
    private string[] configuration;
    private WebApplication? app;

    private EdgeTestHost(int capacity, string[] configuration, Action<IServiceCollection>? configureServices)
    {
        this.capacity = capacity;
        this.configuration = configuration;
        this.configureServices = configureServices;
    }

    public const string Sample = """{"node_id":"virtual-01","seq":1,"timestamp":null,"value":1,"rssi":null}""";
    public static string NodeToken { get; } = new('a', 64);
    public static string OtherToken { get; } = new('b', 64);
    public HttpClient Client { get; private set; } = null!;
    public string DatabasePath => Path.Combine(directory, "test.sqlite");

    public static async Task<EdgeTestHost> StartAsync(int capacity = 100, string[]? configuration = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = new EdgeTestHost(capacity, configuration ?? [], configureServices);
        Directory.CreateDirectory(host.directory);
        try
        {
            await host.StartApplicationAsync();
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async Task RestartAsync(string[]? newConfiguration = null)
    {
        await StopApplicationAsync();
        configuration = newConfiguration ?? configuration;
        await StartApplicationAsync();
    }

    public Task<HttpResponseMessage> PostAsync(string json, string? token, bool chunked = false) =>
        PostAsync(new StringContent(json, Encoding.UTF8, "application/json"), token, chunked);

    public async Task<HttpResponseMessage> PostAsync(HttpContent body, string? token, bool chunked = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry") { Content = body };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (chunked)
        {
            request.Headers.TransferEncodingChunked = true;
        }

        return await Client.SendAsync(request);
    }

    public SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public long Count()
    {
        using var connection = OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_messages;";
        return (long)command.ExecuteScalar()!;
    }

    private async Task StartApplicationAsync()
    {
        app = Program.Build([
            $"--contentRoot={directory}", "--urls=http://127.0.0.1:0",
            $"--Edge:DatabasePath={DatabasePath}", $"--Edge:Capacity={capacity}", "--Edge:WriteTimeoutSeconds=1",
            $"--Edge:Nodes:virtual-01={NodeToken}", $"--Edge:Nodes:esp32-02={OtherToken}",
            "--Logging:LogLevel:Default=Warning", .. configuration
        ], configureServices);
        await app.StartAsync();
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), DefaultRequestVersion = HttpVersion.Version11 };
    }

    private async Task StopApplicationAsync()
    {
        Client?.Dispose();
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
            app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopApplicationAsync();
        Directory.Delete(directory, recursive: true);
    }
}
