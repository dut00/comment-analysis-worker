using System.Globalization;
using System.Text.RegularExpressions;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>
/// One RabbitMQ container per test run (see <see cref="ContainersTestGroup"/>).
/// Tests isolate their messages by using unique exchange and queue names (<see cref="CreateTopologySettings"/>).
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    // The same broker version as docker-compose.yml; update both together.
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4.3.6").Build();

    public Uri Uri => new(_container.GetConnectionString());

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public Task<IConnection> ConnectAsync() => new ConnectionFactory { Uri = Uri }.CreateConnectionAsync();

    public async Task<int> GetConsumerCountAsync(string queue)
    {
        var result = await _container.ExecAsync(
            ["rabbitmqctl", "list_queues", "--quiet", "--no-table-headers", "name", "consumers"]);

        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length == 2 && columns[0] == queue)
            .Select(columns => int.Parse(columns[1], CultureInfo.InvariantCulture))
            .Single();
    }

    /// <summary>Forcibly closes every client connection, as a broker restart or network failure would.</summary>
    public async Task CloseAllConnectionsAsync()
    {
        var result = await _container.ExecAsync(["rabbitmqctl", "close_all_connections", "Closed by integration test"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"close_all_connections failed: {result.Stderr}");
        }
    }

    /// <summary>Applies a RabbitMQ policy to the queue with exactly this name, as an operator would.</summary>
    public async Task SetQueuePolicyAsync(string queue, string definitionJson)
    {
        var result = await _container.ExecAsync(
            ["rabbitmqctl", "set_policy", $"policy-{queue}", $"^{Regex.Escape(queue)}$", definitionJson, "--apply-to", "queues"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"set_policy failed: {result.Stderr}");
        }
    }

    /// <summary>
    /// Ready and unacknowledged message counts as reported by the broker. Unlike a passive queue
    /// declaration, this also shows messages delivered to a consumer but not yet acked.
    /// </summary>
    public async Task<QueueCounts> GetQueueCountsAsync(string queue)
    {
        var result = await _container.ExecAsync(
            ["rabbitmqctl", "list_queues", "--quiet", "--no-table-headers", "name", "messages_ready", "messages_unacknowledged"]);

        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split('\t');
            if (columns.Length == 3 && columns[0] == queue)
            {
                return new QueueCounts(
                    uint.Parse(columns[1], CultureInfo.InvariantCulture),
                    uint.Parse(columns[2], CultureInfo.InvariantCulture));
            }
        }

        throw new InvalidOperationException($"Queue '{queue}' not found. rabbitmqctl output: {result.Stdout} {result.Stderr}");
    }

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

public sealed record QueueCounts(uint Ready, uint Unacknowledged);
