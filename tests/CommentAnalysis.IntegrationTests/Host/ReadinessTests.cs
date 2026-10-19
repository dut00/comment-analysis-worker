using System.Net;
using CommentAnalysis.Infrastructure.Messaging;
using CommentAnalysis.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CommentAnalysis.IntegrationTests.Host;

[Collection(ContainersTestGroup.Name)]
public class ReadinessTests(PostgresFixture postgres, RabbitMqFixture rabbitMq)
{
    private static readonly Uri ReadyUri = new("/health/ready", UriKind.Relative);
    private static readonly Uri LiveUri = new("/health/live", UriKind.Relative);

    [Fact]
    public async Task Ready_ReturnsOk_OnceDependenciesAreReachableAndTheConsumerRuns()
    {
        await using var factory = new WorkerFactory(
        [
            ("Database:ConnectionString", postgres.ConnectionString),
            .. rabbitMq.WorkerSettings(RabbitMqFixture.CreateTopologySettings()),
        ]);
        using var client = factory.CreateClient();

        // Readiness turns healthy only after the topology is declared and the consumer is registered,
        // so poll it as an orchestrator would.
        var status = HttpStatusCode.ServiceUnavailable;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (status != HttpStatusCode.OK && !timeout.IsCancellationRequested)
        {
            status = (await client.GetAsync(ReadyUri)).StatusCode;
            await Task.Delay(100);
        }

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Ready_ReturnsServiceUnavailable_WhenPostgresIsUnreachable_WhileLiveStaysOk()
    {
        // WorkerFactory's default connection string points at a closed port.
        await using var factory = new WorkerFactory(rabbitMq.WorkerSettings(RabbitMqFixture.CreateTopologySettings()));
        using var client = factory.CreateClient();
        await WaitForTopologyAsync(factory);

        var ready = await client.GetAsync(ReadyUri);
        var live = await client.GetAsync(LiveUri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Ready_ReturnsServiceUnavailable_WhenRabbitMqIsUnreachable_WhileLiveStaysOk()
    {
        // WorkerFactory's default RabbitMQ URI points at a closed port.
        await using var factory = new WorkerFactory(("Database:ConnectionString", postgres.ConnectionString));
        using var client = factory.CreateClient();

        var ready = await client.GetAsync(ReadyUri);
        var live = await client.GetAsync(LiveUri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Ready_ReturnsServiceUnavailable_WhenRabbitMqRejectsCredentials()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        var wrongCredentials = new UriBuilder(rabbitMq.Uri) { Password = "wrong-password" }.Uri.ToString();
        await using var factory = new WorkerFactory(
        [
            ("Database:ConnectionString", postgres.ConnectionString),
            .. rabbitMq.WorkerSettings(topology),
            ("RabbitMq:Uri", wrongCredentials),
        ]);
        using var client = factory.CreateClient();
        var initializer = factory.Services.GetRequiredService<RabbitMqTopologyInitializer>();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!initializer.HasConfigurationError && !timeout.IsCancellationRequested)
        {
            await Task.Delay(100);
        }

        Assert.True(initializer.HasConfigurationError, "Expected an authentication failure to count as a configuration error.");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(ReadyUri)).StatusCode);
    }

    private static Task WaitForTopologyAsync(WorkerFactory factory) =>
        factory.Services.GetRequiredService<RabbitMqTopologyInitializer>()
            .WaitUntilReadyAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
}
