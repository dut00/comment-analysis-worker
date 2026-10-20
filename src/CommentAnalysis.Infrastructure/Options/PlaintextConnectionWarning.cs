using CommentAnalysis.Infrastructure.Messaging;
using CommentAnalysis.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CommentAnalysis.Infrastructure.Options;

/// <summary>
/// Warns at startup when <c>AllowPlaintext</c> lets a connection without verified TLS through outside Development, so
/// an opt-out copied from compose into a real deployment shows in the logs. The values themselves are never logged.
/// </summary>
internal sealed partial class PlaintextConnectionWarning(
    IServiceProvider serviceProvider,
    IHostEnvironment environment,
    ILogger<PlaintextConnectionWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            return Task.CompletedTask;
        }

        if (serviceProvider.GetService<IOptions<DatabaseOptions>>()?.Value is { AllowPlaintext: true } database
            && new NpgsqlConnectionStringBuilder(database.ConnectionString).SslMode is var sslMode and not SslMode.VerifyFull)
        {
            LogPlaintextDatabase(logger, environment.EnvironmentName, sslMode);
        }

        if (serviceProvider.GetService<IOptions<RabbitMqOptions>>()?.Value is { AllowPlaintext: true, Uri.Scheme: "amqp" })
        {
            LogPlaintextBroker(logger, environment.EnvironmentName);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Database:AllowPlaintext lets the database connection run without verified TLS (SSL Mode {SslMode}) in the {Environment} environment; use it only on a private network")]
    private static partial void LogPlaintextDatabase(ILogger logger, string environment, SslMode sslMode);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "RabbitMq:AllowPlaintext lets the RabbitMQ connection run without TLS (amqp) in the {Environment} environment; use it only on a private network")]
    private static partial void LogPlaintextBroker(ILogger logger, string environment);
}
