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

    public static WebApplication Build(string[] args)
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
        builder.Services.AddSingleton(credentials);
        builder.Services.AddSingleton<TelemetryValidator>();
        builder.Services.AddSingleton<SqliteTelemetryStore>(services =>
            new SqliteTelemetryStore(settings, services.GetRequiredService<TelemetryValidator>()));
        builder.Services.AddControllers().AddJsonOptions(options =>
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

        var app = builder.Build();
        app.Services.GetRequiredService<SqliteTelemetryStore>().Initialize();
        app.MapControllers();
        return app;
    }
}
