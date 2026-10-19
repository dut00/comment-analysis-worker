using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>
/// One RabbitMQ container per test run (see <see cref="ContainersTestGroup"/>).
/// Tests isolate their messages by using unique exchange and queue names (<see cref="CreateTopologySettings"/>).
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4").Build();

    public Uri Uri => new(_container.GetConnectionString());

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public Task<IConnection> ConnectAsync() => new ConnectionFactory { Uri = Uri }.CreateConnectionAsync();

    /// <summary>Worker settings pointing at this broker with a topology unique to the calling test.</summary>
    public static TopologySettings CreateTopologySettings()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        return new TopologySettings(
            Exchange: $"comments-{suffix}",
            InputQueue: $"comment-created-{suffix}",
            DeadLetterExchange: $"dead-letter-{suffix}",
            DeadLetterQueue: $"comment-created-{suffix}.dlq");
    }

    public (string Key, string? Value)[] WorkerSettings(TopologySettings topology) =>
    [
        ("RabbitMq:Uri", Uri.ToString()),
        ("RabbitMq:Exchange", topology.Exchange),
        ("RabbitMq:InputQueue", topology.InputQueue),
        ("RabbitMq:DeadLetterExchange", topology.DeadLetterExchange),
        ("RabbitMq:DeadLetterQueue", topology.DeadLetterQueue),
    ];
}

public sealed record TopologySettings(string Exchange, string InputQueue, string DeadLetterExchange, string DeadLetterQueue);
