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
    public async Task Ready_ReturnsOk_WhenPostgresAndRabbitMqAreReachable()
    {
        await using var factory = new WorkerFactory(
        [
            ("Database:ConnectionString", postgres.ConnectionString),
            .. rabbitMq.WorkerSettings(RabbitMqFixture.CreateTopologySettings()),
        ]);
        using var client = factory.CreateClient();
        await WaitForTopologyAsync(factory);

        var response = await client.GetAsync(ReadyUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    private static Task WaitForTopologyAsync(WorkerFactory factory) =>
        factory.Services.GetRequiredService<RabbitMqTopologyInitializer>()
            .WaitUntilReadyAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
}
