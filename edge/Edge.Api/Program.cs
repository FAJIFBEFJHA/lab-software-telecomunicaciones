using System.Text.Json;
using Edge.Api.Configuration;
using Edge.Api.Services;
using Edge.Api.Storage;

namespace Edge.Api;

public static class Program
{
    public static async Task Main(string[] args)
    {
        await using var app = Build(args);
        await app.RunAsync();
    }

    public static WebApplication Build(string[] args) => Build(args, null);

    internal static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name
        });

        var edgeSection = builder.Configuration.GetSection("Edge");
        var settings = EdgeSettings.Load(edgeSection, builder.Environment.ContentRootPath);
        var credentials = new NodeCredentials(edgeSection.GetSection("Nodes"));

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(credentials);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<TelemetryValidator>();
        builder.Services.AddSingleton<SqliteTelemetryStore>(services =>
            new SqliteTelemetryStore(settings, services.GetRequiredService<TelemetryValidator>(),
                services.GetRequiredService<TimeProvider>()));
        builder.Services.AddHttpClient(BackendTelemetryClient.ClientName, client =>
        {
            client.BaseAddress = settings.Delivery.BackendUrl;
            client.Timeout = Timeout.InfiniteTimeSpan; // The delivery deadline also covers receipt bodies.
        }).ConfigurePrimaryHttpMessageHandler(() => BackendTelemetryClient.CreateHandler(settings.Delivery))
            .RemoveAllLoggers();
        builder.Services.AddSingleton<BackendTelemetryClient>();
        builder.Services.AddHostedService<TelemetryDeliveryWorker>();
        builder.Services.AddControllers().AddJsonOptions(options =>
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        app.Services.GetRequiredService<SqliteTelemetryStore>().Initialize();
        app.MapControllers();
        return app;
    }
}
