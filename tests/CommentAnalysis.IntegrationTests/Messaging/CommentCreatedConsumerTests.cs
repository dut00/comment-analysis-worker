using System.Text;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Messages;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Messaging;
using CommentAnalysis.Infrastructure.Persistence;
using CommentAnalysis.IntegrationTests.Infrastructure;
using CommentAnalysis.Worker.Consumers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CommentAnalysis.IntegrationTests.Messaging;

[Collection(ContainersTestGroup.Name)]
public class CommentCreatedConsumerTests(PostgresFixture postgres, RabbitMqFixture rabbitMq)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task PublishedComment_IsAnalyzedStoredAndAcked()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology, ("Model:Static:Label", "Unacceptable"), ("Model:Static:Confidence", "0.96"));
        var comment = CreateComment();

        await PublishAsync(topology, MessageSerializer.Serialize(comment));

        var row = await WaitForAnalysisAsync(comment.CommentId);
        Assert.Equal(Decision.Reject, row.Decision);
        Assert.Equal(0.96, row.Confidence);
        Assert.Equal("static@1.0.0", row.ModelVersion);
        Assert.Equal(comment.PostId, row.PostId);
        await AssertQueuesAsync(topology, input: 0, deadLetter: 0);
    }

    [Fact]
    public async Task DuplicateMessages_ProduceOneAnalysis_AndAreAllAcked()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology);
        var body = MessageSerializer.Serialize(CreateComment(out var commentId));

        await PublishAsync(topology, body);
        await PublishAsync(topology, body);
        await PublishAsync(topology, body);

        await WaitForAnalysisAsync(commentId);
        await AssertQueuesAsync(topology, input: 0, deadLetter: 0);
        Assert.Equal(1, await CountAnalysesAsync(commentId));
    }

    [Fact]
    public async Task MalformedMessage_IsDeadLettered_AndDoesNotBlockTheQueue()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology);
        var comment = CreateComment();

        await PublishAsync(topology, Encoding.UTF8.GetBytes("""{ "commentId": "not-a-guid" }"""));
        await PublishAsync(topology, MessageSerializer.Serialize(comment));

        await WaitForAnalysisAsync(comment.CommentId);
        await AssertQueuesAsync(topology, input: 0, deadLetter: 1);
    }

    [Fact]
    public async Task OversizedMessage_IsDeadLettered_WithoutBeingProcessed()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology, ("Consumer:MaxMessageBytes", "1024"), ("Consumer:MaxContentLength", "1024"));
        var oversized = CreateComment() with { Content = new string('a', 2000) };
        var comment = CreateComment();

        await PublishAsync(topology, MessageSerializer.Serialize(oversized));
        await PublishAsync(topology, MessageSerializer.Serialize(comment));

        await WaitForAnalysisAsync(comment.CommentId);
        await AssertQueuesAsync(topology, input: 0, deadLetter: 1);
        Assert.Equal(0, await CountAnalysesAsync(oversized.CommentId));
    }

    [Fact]
    public async Task TooLongContent_IsDeadLettered_WithoutReachingTheModel()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology, ("Consumer:MaxContentLength", "10"));
        var tooLong = CreateComment() with { Content = "eleven char" };
        var comment = CreateComment() with { Content = "ten chars!" };

        await PublishAsync(topology, MessageSerializer.Serialize(tooLong));
        await PublishAsync(topology, MessageSerializer.Serialize(comment));

        await WaitForAnalysisAsync(comment.CommentId);
        await AssertQueuesAsync(topology, input: 0, deadLetter: 1);
        Assert.Equal(0, await CountAnalysesAsync(tooLong.CommentId));
    }

    [Fact]
    public async Task Shutdown_LetsInFlightMessageFinish_AndAcksIt()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        var model = new BlockingDecisionModel();
        var worker = await StartWorkerAsync(topology, model, ("Host:ShutdownTimeout", "00:00:20"));
        var comment = CreateComment();

        await PublishAsync(topology, MessageSerializer.Serialize(comment));
        Assert.True(await WaitUntilAsync(() => Task.FromResult(model.StartedCount == 1)), "The model was never called.");

        // Stop the host while the comment is being analyzed, then let the analysis finish.
        var stopping = worker.DisposeAsync().AsTask();
        await Task.Delay(500);
        Assert.False(stopping.IsCompleted, "Shutdown must wait for the in-flight message.");
        model.Release();
        await stopping.WaitAsync(Timeout);

        Assert.Equal(1, await CountAnalysesAsync(comment.CommentId));
        await AssertQueuesAsync(topology, input: 0, deadLetter: 0);
    }

    [Fact]
    public async Task Shutdown_AbortsInFlightMessageAfterTimeout_AndItIsRedelivered()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        var model = new BlockingDecisionModel();
        var worker = await StartWorkerAsync(topology, model, ("Host:ShutdownTimeout", "00:00:01"));
        var comment = CreateComment();

        await PublishAsync(topology, MessageSerializer.Serialize(comment));
        Assert.True(await WaitUntilAsync(() => Task.FromResult(model.StartedCount == 1)), "The model was never called.");

        // The model never finishes: shutdown gives up after the timeout and must not ack the message.
        await worker.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal(0, await CountAnalysesAsync(comment.CommentId));
        await AssertQueuesAsync(topology, input: 1, deadLetter: 0);
    }

    [Fact]
    public async Task Shutdown_WithConcurrency_LetsAllInFlightMessagesFinish()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        var model = new BlockingDecisionModel();
        var worker = await StartWorkerAsync(topology, model, ("Consumer:Concurrency", "2"), ("Host:ShutdownTimeout", "00:00:20"));
        var first = CreateComment();
        var second = CreateComment();

        await PublishAsync(topology, MessageSerializer.Serialize(first));
        await PublishAsync(topology, MessageSerializer.Serialize(second));
        Assert.True(await WaitUntilAsync(() => Task.FromResult(model.StartedCount == 2)), "Both messages should be in flight.");

        var stopping = worker.DisposeAsync().AsTask();
        await Task.Delay(500);
        Assert.False(stopping.IsCompleted, "Shutdown must wait for both in-flight messages.");
        model.Release();
        await stopping.WaitAsync(Timeout);

        Assert.Equal(1, await CountAnalysesAsync(first.CommentId));
        Assert.Equal(1, await CountAnalysesAsync(second.CommentId));
        await AssertQueuesAsync(topology, input: 0, deadLetter: 0);
    }

    [Fact]
    public async Task TransientFailure_IsRetried_AndTheMessageIsProcessed()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        var model = new FailingDecisionModel(failures: 1);
        await using var worker = await StartWorkerAsync(topology, model, ("Consumer:RetryDelay", "00:00:00"));
        var comment = CreateComment();

        await PublishAsync(topology, MessageSerializer.Serialize(comment));

        await WaitForAnalysisAsync(comment.CommentId);
        Assert.Equal(2, model.Calls);
        await AssertQueuesAsync(topology, input: 0, deadLetter: 0);
    }

    [Fact]
    public async Task PersistentFailure_IsDeadLettered_AfterMaxRetries()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        var model = new FailingDecisionModel(failures: int.MaxValue);
        await using var worker = await StartWorkerAsync(
            topology, model, ("Consumer:MaxRetries", "2"), ("Consumer:RetryDelay", "00:00:00"));
        var comment = CreateComment();

        await PublishAsync(topology, MessageSerializer.Serialize(comment));

        await AssertQueuesAsync(topology, input: 0, deadLetter: 1);
        Assert.Equal(3, model.Calls);
        Assert.Equal(0, await CountAnalysesAsync(comment.CommentId));

        // The dead-lettered copy is the last retry, so it carries the retry count for inspection.
        await using var connection = await rabbitMq.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var deadLettered = await channel.BasicGetAsync(topology.DeadLetterQueue, autoAck: true);
        Assert.NotNull(deadLettered);
        Assert.Equal(2, CommentCreatedConsumer.GetRetryCount(deadLettered.BasicProperties));
        Assert.Equal(comment.CommentId, MessageSerializer.Deserialize<CommentCreated>(deadLettered.Body.Span).CommentId);
    }

    [Fact]
    public async Task RetryCopy_IsNotRoutedToQueuesNamedInTheCcHeader()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        var model = new FailingDecisionModel(failures: 1);
        await using var worker = await StartWorkerAsync(topology, model, ("Consumer:RetryDelay", "00:00:00"));
        var comment = CreateComment();

        // A hostile producer names another queue in CC; the worker's retry must not deliver there.
        await PublishAsync(topology, MessageSerializer.Serialize(comment), new Dictionary<string, object?>
        {
            ["CC"] = new List<object> { Encoding.UTF8.GetBytes(topology.DeadLetterQueue) },
        });

        await WaitForAnalysisAsync(comment.CommentId);
        Assert.Equal(2, model.Calls);
        await AssertQueuesAsync(topology, input: 0, deadLetter: 0);
    }

    [Fact]
    public async Task Consumer_IsRecreated_AfterBrokerCancelsIt()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology);
        var consumer = worker.Services.GetRequiredService<CommentCreatedConsumer>();
        Assert.True(await WaitUntilAsync(() => Task.FromResult(consumer.IsConsuming)), "The consumer never started.");

        // Deleting the queue makes the broker cancel the consumer; the client does not recover from that.
        await using var connection = await rabbitMq.ConnectAsync();
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.QueueDeleteAsync(topology.InputQueue);
        }

        Assert.True(await WaitUntilAsync(() => Task.FromResult(!consumer.IsConsuming)), "The lost consumer was not detected.");

        // Recreate the topology as an operator (or another worker instance) would.
        await using (var channel = await connection.CreateChannelAsync())
        {
            var options = worker.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
            await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);
        }

        Assert.True(await WaitUntilAsync(() => Task.FromResult(consumer.IsConsuming)), "The consumer was not recreated.");
        Assert.Equal(2, consumer.ConsumerStarts);
        var comment = CreateComment();
        await PublishAsync(topology, MessageSerializer.Serialize(comment));
        await WaitForAnalysisAsync(comment.CommentId);

        // The broker-cancelled consumer must not come back through connection recovery next to the new one.
        await rabbitMq.CloseAllConnectionsAsync();
        var afterRecovery = CreateComment();
        await PublishAsync(topology, MessageSerializer.Serialize(afterRecovery));
        await WaitForAnalysisAsync(afterRecovery.CommentId);
        await AssertSingleConsumerAsync(topology);
    }

    [Fact]
    public async Task InFlightMessage_OnLostChannel_DoesNotStallTheRecreatedConsumer()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        var model = new BlockingDecisionModel();
        await using var worker = await StartWorkerAsync(topology, model);
        var consumer = worker.Services.GetRequiredService<CommentCreatedConsumer>();
        var first = CreateComment();
        var second = CreateComment();

        await PublishAsync(topology, MessageSerializer.Serialize(first));
        Assert.True(await WaitUntilAsync(() => Task.FromResult(model.StartedCount == 1)), "The model was never called.");

        // Break the consumer's channel while the first message is in flight: acking an unknown delivery
        // tag makes the broker close the channel (406), as consumer_timeout would.
        await consumer.CurrentChannel!.BasicAckAsync(deliveryTag: 999_999, multiple: false);
        await Task.Delay(TimeSpan.FromSeconds(2));

        // The old handler still holds the only slot; deliveries on a recreated channel must wait for it,
        // not be dropped and held unacked.
        await PublishAsync(topology, MessageSerializer.Serialize(second));
        model.Release();

        await WaitForAnalysisAsync(first.CommentId);
        await WaitForAnalysisAsync(second.CommentId);
        await AssertQueuesAsync(topology, input: 0, deadLetter: 0);
        Assert.Equal(1, await CountAnalysesAsync(first.CommentId));
        Assert.Equal(2, consumer.ConsumerStarts);
    }

    [Fact]
    public async Task Consumer_KeepsWorking_AfterConnectionIsDropped()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology);
        var before = CreateComment();
        await PublishAsync(topology, MessageSerializer.Serialize(before));
        await WaitForAnalysisAsync(before.CommentId);

        // Automatic connection recovery restores the channel and the consumer.
        await rabbitMq.CloseAllConnectionsAsync();

        var after = CreateComment();
        await PublishAsync(topology, MessageSerializer.Serialize(after));
        await WaitForAnalysisAsync(after.CommentId);
        await AssertQueuesAsync(topology, input: 0, deadLetter: 0);

        // Recovery restored the original consumer: the worker neither added a second one nor recreated it.
        var consumer = worker.Services.GetRequiredService<CommentCreatedConsumer>();
        await AssertSingleConsumerAsync(topology);
        Assert.Equal(1, consumer.ConsumerStarts);
        Assert.True(consumer.IsConsuming);
    }

    private async Task<WorkerFactory> StartWorkerAsync(TopologySettings topology, params (string Key, string? Value)[] settings) =>
        await StartWorkerAsync(topology, model: null, settings);

    private async Task<WorkerFactory> StartWorkerAsync(
        TopologySettings topology,
        IDecisionModel? model,
        params (string Key, string? Value)[] settings)
    {
        var factory = new WorkerFactory(
        [
            ("Database:ConnectionString", postgres.ConnectionString),
            .. rabbitMq.WorkerSettings(topology),
            .. settings,
        ])
        {
            ConfigureServices = model is null ? null : services => services.Replace(ServiceDescriptor.Singleton(model)),
        };

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

    private async Task PublishAsync(TopologySettings topology, byte[] body, IDictionary<string, object?>? headers = null)
    {
        await using var connection = await rabbitMq.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await channel.BasicPublishAsync(
            topology.Exchange,
            "comment.created",
            mandatory: true,
            new BasicProperties { ContentType = "application/json", Persistent = true, Headers = headers },
            body);
    }

    private async Task<CommentAnalysisEntity> WaitForAnalysisAsync(Guid commentId)
    {
        CommentAnalysisEntity? row = null;
        await WaitUntilAsync(async () =>
        {
            await using var context = postgres.CreateContext();
            row = await context.CommentAnalyses.AsNoTracking().SingleOrDefaultAsync(a => a.CommentId == commentId);
            return row is not null;
        });

        return row ?? throw new TimeoutException($"No analysis stored for comment {commentId}.");
    }

    private async Task<int> CountAnalysesAsync(Guid commentId)
    {
        await using var context = postgres.CreateContext();
        return await context.CommentAnalyses.CountAsync(a => a.CommentId == commentId);
    }

    /// <summary>
    /// Asserts ready message counts and that nothing is left unacknowledged in the input queue.
    /// Waits first, because dead-lettering and returns of unacked messages are asynchronous in quorum queues.
    /// </summary>
    private async Task AssertQueuesAsync(TopologySettings topology, uint input, uint deadLetter)
    {
        var expectedInput = new QueueCounts(input, 0);
        var expectedDeadLetter = new QueueCounts(deadLetter, 0);
        QueueCounts? actualInput = null;
        QueueCounts? actualDeadLetter = null;

        await WaitUntilAsync(async () =>
        {
            actualInput = await rabbitMq.GetQueueCountsAsync(topology.InputQueue);
            actualDeadLetter = await rabbitMq.GetQueueCountsAsync(topology.DeadLetterQueue);
            return actualInput == expectedInput && actualDeadLetter == expectedDeadLetter;
        });

        Assert.Equal(expectedInput, actualInput);
        Assert.Equal(expectedDeadLetter, actualDeadLetter);
    }

    /// <summary>
    /// Asserts exactly one consumer on the input queue. Quorum queues report consumers with a short delay
    /// after recovery, so wait for one, then check that no second one appears.
    /// </summary>
    private async Task AssertSingleConsumerAsync(TopologySettings topology)
    {
        var observed = new List<int>();
        await WaitUntilAsync(async () =>
        {
            observed.Add(await rabbitMq.GetConsumerCountAsync(topology.InputQueue));
            return observed[^1] == 1;
        });

        await Task.Delay(TimeSpan.FromSeconds(2));
        observed.Add(await rabbitMq.GetConsumerCountAsync(topology.InputQueue));

        Assert.True(observed[^2] == 1 && observed[^1] == 1, $"Expected one consumer; observed {string.Join(", ", observed)}.");
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!await condition() && !timeout.IsCancellationRequested)
        {
            await Task.Delay(100);
        }

        return await condition();
    }

    private static CommentCreated CreateComment() => CreateComment(out _);

    private static CommentCreated CreateComment(out Guid commentId)
    {
        commentId = Guid.NewGuid();
        return new CommentCreated
        {
            CommentId = commentId,
            PostId = Guid.NewGuid(),
            AuthorId = Guid.NewGuid(),
            Content = "Great post, thanks!",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
