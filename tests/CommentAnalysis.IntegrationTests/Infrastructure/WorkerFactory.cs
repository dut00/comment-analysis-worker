using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the worker in-process with settings that need no external services: a static model
/// and a connection string that is never opened (migrations on startup are disabled).
/// Individual settings can be overridden per test.
/// </summary>
internal sealed class WorkerFactory : WebApplicationFactory<Program>
{
    private static readonly Dictionary<string, string?> Defaults = new()
    {
        ["Model:Provider"] = "Static",
        ["Database:ConnectionString"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
        ["Database:MigrateOnStartup"] = "false",
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
