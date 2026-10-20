using CommentAnalysis.Infrastructure.Messaging;
using CommentAnalysis.Infrastructure.Options;
using CommentAnalysis.Infrastructure.Persistence;
using CommentAnalysis.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.IntegrationTests.Host;

/// <summary>Outside Development, the database and the broker must be reached over verified TLS.</summary>
public class TransportSecurityTests
{
    private const string PlaintextDatabase = "Host=db.example.com;Database=comment_analysis;Username=worker;Password=secret";

    [Theory]
    [InlineData("Production", PlaintextDatabase, false, false)]
    [InlineData("Production", PlaintextDatabase + ";SSL Mode=Require", false, false)]
    [InlineData("Production", PlaintextDatabase + ";SSL Mode=VerifyCA", false, false)]
    [InlineData("Production", PlaintextDatabase + ";SSL Mode=VerifyFull", false, true)]
    [InlineData("Production", PlaintextDatabase, true, true)]
    [InlineData("Staging", PlaintextDatabase, false, false)]
    [InlineData("Development", PlaintextDatabase, false, true)]
    [InlineData("Production", PlaintextDatabase + ";SslMode=VerifyFull", false, true)]
    [InlineData("Production", PlaintextDatabase + ";sslmode=verifyfull", false, true)]
    [InlineData("Production", PlaintextDatabase + ";SSL Mode=Disable", false, false)]
    [InlineData("Production", PlaintextDatabase + ";SSL Mode=Disable", true, true)]
    [InlineData("Production", "Host=db1.example.com,db2.example.com;Database=comment_analysis;Username=worker;Password=secret;SSL Mode=VerifyFull", false, true)]
    public void Database_RequiresVerifiedTls_OutsideDevelopment(string environment, string connectionString, bool allowPlaintext, bool valid)
    {
        var result = new DatabaseOptionsValidator(Environment(environment))
            .Validate(null, new DatabaseOptions { ConnectionString = connectionString, AllowPlaintext = allowPlaintext });

        Assert.Equal(valid, result.Succeeded);
    }

    [Theory]
    [InlineData("Host=db;Password=secret;Nonsense Key=1")]
    [InlineData("Host=db;Port=abc;Password=secret")]
    [InlineData("Host=db;Password=secret;SSL Mode=verify-full")]
    public void Database_InvalidConnectionString_IsReportedWithoutItsContent(string connectionString)
    {
        // libpq's "verify-full" is not an Npgsql value: the operator gets "not valid", not a silent fallback.
        var result = new DatabaseOptionsValidator(Environment("Production"))
            .Validate(null, new DatabaseOptions { ConnectionString = connectionString });

        Assert.True(result.Failed);
        Assert.DoesNotContain("secret", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production", "amqp://worker:secret@mq.example.com:5672/", false, false)]
    [InlineData("Production", "amqps://worker:secret@mq.example.com:5671/", false, true)]
    [InlineData("Production", "amqp://guest:guest@rabbitmq:5672/", true, true)]
    [InlineData("Development", "amqp://guest:guest@localhost:5672/", false, true)]
    public void RabbitMq_RequiresTls_OutsideDevelopment(string environment, string uri, bool allowPlaintext, bool valid)
    {
        var result = new RabbitMqOptionsValidator(Environment(environment))
            .Validate(null, new RabbitMqOptions { Uri = new Uri(uri), AllowPlaintext = allowPlaintext });

        Assert.Equal(valid, result.Succeeded);
    }

    [Theory]
    [InlineData("Database:AllowPlaintext", "Database:ConnectionString must use SSL Mode=VerifyFull")]
    [InlineData("RabbitMq:AllowPlaintext", "RabbitMq:Uri must use amqps")]
    public async Task Startup_Fails_OutsideDevelopment_WithAPlaintextConnection(string allowPlaintext, string expectedMessage)
    {
        // WorkerFactory's addresses are plaintext; without the opt-out, Production refuses them.
        await using var factory = new WorkerFactory(("Model:Provider", "Static"), (allowPlaintext, null)) { Environment = "Production" };

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsingTheOptOut_OutsideDevelopment_IsLogged()
    {
        // WorkerFactory sets both opt-outs for its plaintext addresses.
        var logs = new LogCollector();
        await using var factory = new WorkerFactory(("Model:Provider", "Static"))
        {
            Environment = "Production",
            ConfigureServices = services => services.AddSingleton<ILoggerProvider>(logs),
        };

        factory.CreateClient().Dispose();

        Assert.Contains(logs.Messages, message => message.StartsWith("Database:AllowPlaintext", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, message => message.StartsWith("RabbitMq:AllowPlaintext", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    private static HostingEnvironment Environment(string name) => new() { EnvironmentName = name };

    private sealed class LogCollector : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Messages.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
