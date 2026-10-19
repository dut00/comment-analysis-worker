using System.Globalization;
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
    public async Task MessageThatKeepsCrashingConsumers_IsDeadLetteredAfterDeliveryLimit()
    {
        const int deliveryLimit = 2;
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology, ("RabbitMq:DeliveryLimit", deliveryLimit.ToString(CultureInfo.InvariantCulture)));

        await using var connection = await rabbitMq.ConnectAsync();
        await using (var publisher = await connection.CreateChannelAsync())
        {
            await PublishAsync(publisher, topology.Exchange, "comment.created");
            await WaitForMessageCountAsync(publisher, topology.InputQueue, expected: 1);
        }

        // Simulate a poison message that crashes the consumer: the channel closes without an ack.
        // (In RabbitMQ 4 an explicit nack/reject with requeue does NOT count towards the limit.)
        await using var channel = await connection.CreateChannelAsync();
        var deliveries = 0;
        for (var attempt = 0; attempt <= deliveryLimit + 1; attempt++)
        {
            // Returning an unacked message to a quorum queue is asynchronous: wait until it is back
            // in the input queue (or has been dead-lettered) before trying to get it again.
            var available = await WaitUntilAsync(async () =>
                await channel.MessageCountAsync(topology.InputQueue) > 0 ||
                await channel.MessageCountAsync(topology.DeadLetterQueue) > 0);
            if (!available || await channel.MessageCountAsync(topology.InputQueue) == 0)
            {
                break;
            }

            await using var consumer = await connection.CreateChannelAsync();
            Assert.NotNull(await consumer.BasicGetAsync(topology.InputQueue, autoAck: false));
            deliveries++;
        }

        Assert.Equal(1u, await WaitForMessageCountAsync(channel, topology.DeadLetterQueue, expected: 1));
        Assert.Equal(deliveryLimit + 1, deliveries);
    }

    [Fact]
    public async Task ExistingQueueWithDifferentArguments_IsReportedAsConfigurationError()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using (var connection = await rabbitMq.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            // Someone created the input queue as a classic queue before the worker started.
            await channel.QueueDeclareAsync(topology.InputQueue, durable: true, exclusive: false, autoDelete: false);
        }

        await using var factory = new WorkerFactory(rabbitMq.WorkerSettings(topology));
        var initializer = factory.Services.GetRequiredService<RabbitMqTopologyInitializer>();

        Assert.True(await WaitUntilAsync(() => initializer.HasConfigurationError), "Expected a configuration error.");
        Assert.False(initializer.IsReady);
    }

    [Fact]
    public async Task Topology_IsIdempotent_WhenSecondWorkerStarts()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();

        await using var first = await StartWorkerAsync(topology);
        await using var second = await StartWorkerAsync(topology);
    }

    private async Task<WorkerFactory> StartWorkerAsync(TopologySettings topology, params (string Key, string? Value)[] settings)
    {
        var factory = new WorkerFactory([.. rabbitMq.WorkerSettings(topology), .. settings]);
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

    private static Task<bool> WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!await condition() && !timeout.IsCancellationRequested)
        {
            await Task.Delay(100);
        }

        return await condition();
    }

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
