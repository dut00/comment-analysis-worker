using System.Text;
using CommentAnalysis.Infrastructure.Messaging;
using CommentAnalysis.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace CommentAnalysis.IntegrationTests.Messaging;

[Collection(ContainersTestGroup.Name)]
public class RabbitMqTopologyTests(RabbitMqFixture rabbitMq)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Worker_DeclaresQueues_AndRoutesCommentCreatedToInputQueue()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology);

        await using var connection = await rabbitMq.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        await PublishAsync(channel, topology.Exchange, "comment.created");
        await PublishAsync(channel, topology.Exchange, "comment.updated");

        Assert.Equal(1u, await WaitForMessageCountAsync(channel, topology.InputQueue, expected: 1));
        Assert.Equal(0u, await channel.MessageCountAsync(topology.DeadLetterQueue));
    }

    [Fact]
    public async Task RejectedMessage_IsDeadLetteredToDlq()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology);

        await using var connection = await rabbitMq.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await PublishAsync(channel, topology.Exchange, "comment.created");
        await WaitForMessageCountAsync(channel, topology.InputQueue, expected: 1);

        var delivery = await channel.BasicGetAsync(topology.InputQueue, autoAck: false);
        Assert.NotNull(delivery);
        await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);

        Assert.Equal(1u, await WaitForMessageCountAsync(channel, topology.DeadLetterQueue, expected: 1));
        Assert.Equal(0u, await channel.MessageCountAsync(topology.InputQueue));
    }

    [Fact]
    public async Task Topology_IsIdempotent_WhenSecondWorkerStarts()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();

        await using var first = await StartWorkerAsync(topology);
        await using var second = await StartWorkerAsync(topology);
    }

    private async Task<WorkerFactory> StartWorkerAsync(TopologySettings topology)
    {
        var factory = new WorkerFactory(rabbitMq.WorkerSettings(topology));
        try
        {
            await factory.Services.GetRequiredService<RabbitMqTopologyInitializer>()
                .WaitUntilReadyAsync(CancellationToken.None)
                .WaitAsync(Timeout);

            return factory;
        }
        catch
        {
            await factory.DisposeAsync();
            throw;
        }
    }

    private static async Task PublishAsync(IChannel channel, string exchange, string routingKey) =>
        await channel.BasicPublishAsync(exchange, routingKey, Encoding.UTF8.GetBytes("{}"));

    private static async Task<uint> WaitForMessageCountAsync(IChannel channel, string queue, uint expected)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var count = await channel.MessageCountAsync(queue);
            if (count >= expected || timeout.IsCancellationRequested)
            {
                return count;
            }

            await Task.Delay(100);
        }
    }
}
