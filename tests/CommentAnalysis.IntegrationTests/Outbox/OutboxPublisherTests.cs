using System.Diagnostics.Metrics;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Messages;
using CommentAnalysis.Infrastructure.Messaging;
using CommentAnalysis.Infrastructure.Persistence;
using CommentAnalysis.IntegrationTests.Infrastructure;
using CommentAnalysis.Worker.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using RabbitMQ.Client;

namespace CommentAnalysis.IntegrationTests.Outbox;

[Collection(ContainersTestGroup.Name)]
public class OutboxPublisherTests(PostgresFixture postgres, RabbitMqFixture rabbitMq)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task CommentCreated_In_CommentAnalyzed_Out()
    {
        var topology = RabbitMqFixture.CreateTopologySettings();
        await using var worker = await StartWorkerAsync(topology, ("Model:Static:Label", "Acceptable"), ("Model:Static:Confidence", "0.91"));
        using var published = new MetricCollector<long>(
            worker.Services.GetRequiredService<IMeterFactory>(), WorkerMetrics.MeterName, "comment_analysis.outbox.published");
        await using var connection = await rabbitMq.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var subscriber = await SubscribeAsync(channel, topology);
        var comment = CreateComment();

        await PublishCommentAsync(channel, topology, comment);

        var (delivery, analyzed) = await ReceiveAnalyzedAsync(channel, subscriber, comment.CommentId);
        Assert.Equal(comment.PostId, analyzed.PostId);
        Assert.Equal(Decision.Approve, analyzed.Decision);
        Assert.Equal(0.91, analyzed.Confidence);
        Assert.Equal("static@1.0.0", analyzed.ModelVersion);
        Assert.Equal(CommentAnalyzed.MessageType, delivery.BasicProperties.Type);
        Assert.Equal("application/json", delivery.BasicProperties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, delivery.BasicProperties.DeliveryMode);

        // The AMQP message id is the outbox row id, which is marked as published.
        var row = await WaitForPublishedAsync(Guid.Parse(delivery.BasicProperties.MessageId!));
        Assert.NotNull(row.PublishedAt);

        // Other tests' rows in the shared database may be published by this worker too.
        await published.WaitForMeasurementsAsync(1).WaitAsync(Timeout);
        Assert.True(published.GetMeasurementSnapshot().EvaluateAsCounter() >= 1);
    }

    [Fact]
    public async Task OutboxLag_IsReported_WhilePublishingIsStuck()
    {
        var oldest = DateTimeOffset.UtcNow.AddHours(-1);
        var row = OutboxMessageEntity.Create(CommentAnalyzed.MessageType, new { id = Guid.NewGuid() }, oldest);
        await using (var context = postgres.CreateContext())
        {
            context.OutboxMessages.Add(row);
            await context.SaveChangesAsync();
        }

        try
        {
            // RabbitMQ on WorkerFactory's closed port: nothing is published, so the row keeps waiting.
            await using var worker = new WorkerFactory(("Database:ConnectionString", postgres.ConnectionString));
            using var lag = new MetricCollector<double>(
                worker.Services.GetRequiredService<IMeterFactory>(), WorkerMetrics.MeterName, "comment_analysis.outbox.oldest_pending_age");

            // Older rows of other tests can only make the reported lag larger.
            var reported = 0.0;
            using var timeout = new CancellationTokenSource(Timeout);
            while (reported < (DateTimeOffset.UtcNow - oldest).TotalSeconds - 5 && !timeout.IsCancellationRequested)
            {
                await Task.Delay(100);
                lag.RecordObservableInstruments();
                reported = lag.LastMeasurement?.Value ?? 0;
            }

            Assert.InRange(reported, (DateTimeOffset.UtcNow - oldest).TotalSeconds - 5, double.MaxValue);
        }
        finally
        {
            await using var context = postgres.CreateContext();
            await context.OutboxMessages.Where(m => m.Id == row.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task MessageOfUnknownType_IsLeftUnpublished_AndDoesNotBlockOthers()
    {
        // Older than anything else, so with a batch size of 1 it would take the only slot if it were claimed.
        var unknown = OutboxMessageEntity.Create("SomethingFromANewerVersion", new { id = Guid.NewGuid() }, DateTimeOffset.UtcNow.AddYears(-1));
        await using (var context = postgres.CreateContext())
        {
            context.OutboxMessages.Add(unknown);
            await context.SaveChangesAsync();
        }

        try
        {
            var topology = RabbitMqFixture.CreateTopologySettings();
            await using var worker = await StartWorkerAsync(topology, ("Outbox:BatchSize", "1"));
            await using var connection = await rabbitMq.ConnectAsync();
            await using var channel = await connection.CreateChannelAsync();
            var subscriber = await SubscribeAsync(channel, topology);
            var comment = CreateComment();

            await PublishCommentAsync(channel, topology, comment);

            await ReceiveAnalyzedAsync(channel, subscriber, comment.CommentId);
            Assert.Null((await FindRowAsync(unknown.Id))?.PublishedAt);
        }
        finally
        {
            await using var context = postgres.CreateContext();
            await context.OutboxMessages.Where(m => m.Id == unknown.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Cleanup_DeletesOnlyMessagesPublishedBeforeTheRetentionPeriod()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = CreatePublishedRow(publishedAt: now.AddDays(-2));
        var retained = CreatePublishedRow(publishedAt: now.AddHours(-1));
        await using (var context = postgres.CreateContext())
        {
            context.OutboxMessages.AddRange(expired, retained);
            await context.SaveChangesAsync();
        }

        await using var worker = await StartWorkerAsync(
            RabbitMqFixture.CreateTopologySettings(), ("Outbox:Retention", "1.00:00:00"));

        Assert.True(await WaitUntilAsync(async () => await FindRowAsync(expired.Id) is null), "The expired message was not deleted.");
        Assert.NotNull(await FindRowAsync(retained.Id));
    }

    private static CommentCreated CreateComment() => new()
    {
        CommentId = Guid.NewGuid(),
        PostId = Guid.NewGuid(),
        AuthorId = Guid.NewGuid(),
        Content = "Great post, thanks!",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task PublishCommentAsync(IChannel channel, TopologySettings topology, CommentCreated comment) =>
        await channel.BasicPublishAsync(
            topology.Exchange,
            "comment.created",
            mandatory: true,
            new BasicProperties { ContentType = "application/json", Persistent = true },
            MessageSerializer.Serialize(comment));

    private static OutboxMessageEntity CreatePublishedRow(DateTimeOffset publishedAt)
    {
        var row = OutboxMessageEntity.Create(
            CommentAnalyzed.MessageType,
            new CommentAnalyzed
            {
                CommentId = Guid.NewGuid(),
                PostId = Guid.NewGuid(),
                Decision = Decision.ManualReview,
                Confidence = 0.6,
                ModelName = "static",
                ModelVersion = "static@1.0.0",
                AnalyzedAt = publishedAt,
            },
            publishedAt);
        row.PublishedAt = publishedAt;
        return row;
    }

    private async Task<WorkerFactory> StartWorkerAsync(TopologySettings topology, params (string Key, string? Value)[] settings)
    {
        var factory = new WorkerFactory(
        [
            ("Database:ConnectionString", postgres.ConnectionString),
            .. rabbitMq.WorkerSettings(topology),
            .. settings,
        ]);

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

    private static async Task<string> SubscribeAsync(IChannel channel, TopologySettings topology)
    {
        var queue = await channel.QueueDeclareAsync(queue: string.Empty, durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(queue.QueueName, topology.Exchange, "comment.analyzed");
        return queue.QueueName;
    }

    /// <summary>
    /// Waits for the event of the given comment. The publisher also publishes messages left in the shared
    /// database by other tests, so unrelated events are skipped.
    /// </summary>
    private static async Task<(BasicGetResult Delivery, CommentAnalyzed Message)> ReceiveAnalyzedAsync(
        IChannel channel,
        string queue,
        Guid commentId)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!timeout.IsCancellationRequested)
        {
            if (await channel.BasicGetAsync(queue, autoAck: true) is { } delivery)
            {
                var message = MessageSerializer.Deserialize<CommentAnalyzed>(delivery.Body.Span);
                if (message.CommentId == commentId)
                {
                    return (delivery, message);
                }
            }
            else
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"No CommentAnalyzed received for comment {commentId}.");
    }

    private async Task<OutboxMessageEntity> WaitForPublishedAsync(Guid id)
    {
        OutboxMessageEntity? row = null;
        await WaitUntilAsync(async () =>
        {
            row = await FindRowAsync(id);
            return row?.PublishedAt is not null;
        });

        return row ?? throw new InvalidOperationException($"Outbox message {id} not found.");
    }

    private async Task<OutboxMessageEntity?> FindRowAsync(Guid id)
    {
        await using var context = postgres.CreateContext();
        return await context.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id);
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
}
