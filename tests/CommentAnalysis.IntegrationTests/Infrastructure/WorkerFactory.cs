using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the worker in-process with settings that need no external services: a static model
/// and PostgreSQL and RabbitMQ addresses on a closed port (migrations on startup are disabled).
/// Individual settings can be overridden per test.
/// </summary>
internal sealed class WorkerFactory : WebApplicationFactory<Program>
{
    private static readonly Dictionary<string, string?> Defaults = new()
    {
        ["Model:Provider"] = "Static",
        // Closed port: fails fast if anything tries to connect (e.g. the readiness check).
        ["Database:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2",
        ["Database:MigrateOnStartup"] = "false",
        ["RabbitMq:Uri"] = "amqp://guest:guest@127.0.0.1:1/",
    };

    private readonly Dictionary<string, string?> _settings;

    public WorkerFactory(params (string Key, string? Value)[] overrides)
    {
        _settings = new Dictionary<string, string?>(Defaults);
        foreach (var (key, value) in overrides)
        {
            _settings[key] = value;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
