using System.Net;
using CommentAnalysis.IntegrationTests.Infrastructure;

namespace CommentAnalysis.IntegrationTests.Host;

[Collection(PostgresTestGroup.Name)]
public class ReadinessTests(PostgresFixture postgres)
{
    private static readonly Uri ReadyUri = new("/health/ready", UriKind.Relative);
    private static readonly Uri LiveUri = new("/health/live", UriKind.Relative);

    [Fact]
    public async Task Ready_ReturnsOk_WhenPostgresIsReachable()
    {
        await using var factory = new WorkerFactory(("Database:ConnectionString", postgres.ConnectionString));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ReadyUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_ReturnsServiceUnavailable_WhenPostgresIsUnreachable_WhileLiveStaysOk()
    {
        // WorkerFactory's default connection string points at a closed port.
        await using var factory = new WorkerFactory();
        using var client = factory.CreateClient();

        var ready = await client.GetAsync(ReadyUri);
        var live = await client.GetAsync(LiveUri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
