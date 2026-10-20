using System.Runtime.ExceptionServices;
using System.Text;
using CommentAnalysis.Core.Messages;
using CommentAnalysis.Infrastructure.Messaging;
using CommentAnalysis.Infrastructure.Persistence;
using CommentAnalysis.Worker.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CommentAnalysis.Worker.Outbox;

/// <summary>
/// Publishes messages from the transactional outbox (<c>outbox_messages</c>) to RabbitMQ and deletes old
/// published ones.
/// </summary>
/// <remarks>
/// <para>
/// Each batch is claimed in one database transaction with <c>FOR UPDATE SKIP LOCKED</c>, so replicas publish
/// different rows. A row is marked as published only after the broker confirmed it. Delivery is at least once:
/// a crash between the confirm and the commit publishes the row again, so consumers deduplicate on the AMQP
/// message id (the row id). The order of messages is not guaranteed across replicas or retries.
/// </para>
/// <para>
/// Messages are published one by one, each awaiting its confirm, while the batch's rows stay locked; concurrent
/// publishes bounded by the channel's outstanding-confirms limit would raise throughput if it becomes a bottleneck.
/// Messages are published without <c>mandatory</c>: an event that no queue subscribes to is dropped by the broker
/// rather than kept in the outbox forever.
/// </para>
/// </remarks>
internal sealed partial class OutboxPublisher(
    [FromKeyedServices(RabbitMqConnection.PublisherName)] RabbitMqConnection connection,
    RabbitMqTopologyInitializer topology,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> rabbitMqOptions,
    IOptions<OutboxOptions> outboxOptions,
    TimeProvider timeProvider,
    WorkerMetrics metrics,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private const int CleanupBatchSize = 1000;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Bounds recording a partly published batch, which also runs during shutdown.</summary>
    private static readonly TimeSpan FinalSaveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Caps closing the channel; counted in the shutdown budget.</summary>
    private static readonly TimeSpan ChannelCloseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Message type → routing key. Rows of other types are never claimed.</summary>
    private readonly Dictionary<string, string> _routingKeys = new(StringComparer.Ordinal)
    {
        [CommentAnalyzed.MessageType] = rabbitMqOptions.Value.OutputRoutingKey,
    };

    /// <summary>The message types this version publishes; rows of other types are left for a version that can.</summary>
    internal static readonly string[] KnownTypes = [CommentAnalyzed.MessageType];

    private IChannel? _channel;
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = outboxOptions.Value;
        var retryDelay = InitialRetryDelay;

        try
        {
            await topology.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);

            while (true)
            {
                var published = 0;
                var failed = false;
                try
                {
                    published = await PublishBatchAsync(settings, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // E.g. the database or RabbitMQ is unavailable. Unconfirmed rows stay unpublished.
                    LogPublishingFailed(logger, exception, retryDelay);
                    await CloseChannelAsync().ConfigureAwait(false);
                    failed = true;
                }

                // Independent of publishing, which may keep failing (e.g. RabbitMQ is down).
                await CleanUpIfDueAsync(settings, stoppingToken).ConfigureAwait(false);

                if (failed)
                {
                    await Task.Delay(retryDelay, timeProvider, stoppingToken).ConfigureAwait(false);
                    retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
                    continue;
                }

                retryDelay = InitialRetryDelay;

                // A full batch suggests more are waiting; otherwise poll again later.
                if (published < settings.BatchSize)
                {
                    await Task.Delay(settings.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: a batch in progress is rolled back and published again by the next run.
        }
        finally
        {
            await CloseChannelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Publishes one batch; returns how many messages were published.</summary>
    private async Task<int> PublishBatchAsync(OutboxOptions settings, CancellationToken stoppingToken)
    {
        // Opened before any row is locked: connecting may take long, and must not hold locks meanwhile.
        var channel = await GetChannelAsync(stoppingToken).ConfigureAwait(false);

        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AnalysisDbContext>();

        // Only known types are claimed: a row this version cannot route (e.g. written by a newer version during
        // a rolling deploy) must not take up batch slots. Such rows are reported by the cleanup.
        await using var transaction = await context.Database.BeginTransactionAsync(stoppingToken).ConfigureAwait(false);
        var messages = await context.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL AND type = ANY({KnownTypes})
                ORDER BY created_at
                LIMIT {settings.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(stoppingToken)
            .ConfigureAwait(false);

        if (messages.Count == 0)
        {
            return 0;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(settings.PublishTimeout);

        // Published one by one, each awaiting its confirm; stops at the first failure.
        ExceptionDispatchInfo? failure = null;
        var publishedAt = timeProvider.GetUtcNow();
        var published = 0;
        foreach (var message in messages)
        {
            try
            {
                await channel.BasicPublishAsync(
                    rabbitMqOptions.Value.Exchange,
                    _routingKeys[message.Type],
                    mandatory: false,
                    CreateProperties(message),
                    Encoding.UTF8.GetBytes(message.Payload),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                break;
            }

            message.PublishedAt = publishedAt;
            published++;
        }

        // Record what the broker confirmed even if shutdown or a failure interrupted the batch. Bounded, so that
        // shutdown does not wait on the database; if it fails, the confirmed rows are simply published again.
        if (published > 0)
        {
            using var saveTimeout = new CancellationTokenSource(FinalSaveTimeout, timeProvider);
            try
            {
                await context.SaveChangesAsync(saveTimeout.Token).ConfigureAwait(false);
                await transaction.CommitAsync(saveTimeout.Token).ConfigureAwait(false);
            }
            catch when (failure is not null)
            {
                // Keep the original cause visible; the save failure propagates.
                LogPublishingInterrupted(logger, failure.SourceException);
                throw;
            }

            LogPublished(logger, published);
            metrics.OutboxPublished(published);
        }

        failure?.Throw();
        return published;
    }


    private static BasicProperties CreateProperties(OutboxMessageEntity message) => new()
    {
        MessageId = message.Id.ToString(),
        Type = message.Type,
        ContentType = "application/json",
        DeliveryMode = DeliveryModes.Persistent,
        Timestamp = new AmqpTimestamp(message.CreatedAt.ToUnixTimeSeconds()),
    };

    private async Task CleanUpIfDueAsync(OutboxOptions settings, CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextCleanup)
        {
            return;
        }

        _nextCleanup = now + settings.CleanupInterval;
        try
        {
            await CleanUpAsync(now - settings.Retention, settings.Retention, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            LogCleanupFailed(logger, exception);
        }
    }

    private async Task CleanUpAsync(DateTimeOffset cutoff, TimeSpan retention, CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AnalysisDbContext>();

        // Deleted in small batches to keep transactions and locks short.
        var total = 0;
        int deleted;
        do
        {
            deleted = await context.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM outbox_messages
                WHERE id IN (SELECT id FROM outbox_messages WHERE published_at < {cutoff} LIMIT {CleanupBatchSize})
                """,
                stoppingToken).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted == CleanupBatchSize);

        if (total > 0)
        {
            LogCleanedUp(logger, total, retention);
        }

        // Reported here, i.e. rarely, rather than on every poll.
        var unknown = await context.OutboxMessages
            .CountAsync(m => m.PublishedAt == null && !KnownTypes.Contains(m.Type), stoppingToken)
            .ConfigureAwait(false);
        if (unknown > 0)
        {
            LogUnknownMessageTypes(logger, unknown);
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } channel)
        {
            return channel;
        }

        await CloseChannelAsync().ConfigureAwait(false);
        var amqp = await connection.GetAsync(cancellationToken).ConfigureAwait(false);
        _channel = await amqp.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);
        return _channel;
    }

    private async Task CloseChannelAsync()
    {
        if (_channel is not { } channel)
        {
            return;
        }

        _channel = null;
        try
        {
            await channel.DisposeAsync().AsTask().WaitAsync(ChannelCloseTimeout, timeProvider).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogChannelCloseFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Debug, Message = "Published {Count} outbox message(s)")]
    private static partial void LogPublished(ILogger logger, int count);

    [LoggerMessage(EventId = 401, Level = LogLevel.Warning, Message = "Publishing outbox messages failed; retrying in {RetryDelay}")]
    private static partial void LogPublishingFailed(ILogger logger, Exception exception, TimeSpan retryDelay);

    [LoggerMessage(EventId = 402, Level = LogLevel.Error, Message = "{Count} outbox message(s) have a type this version cannot publish; they are left for a version that can")]
    private static partial void LogUnknownMessageTypes(ILogger logger, int count);

    [LoggerMessage(EventId = 403, Level = LogLevel.Information, Message = "Deleted {Count} outbox message(s) published more than {Retention} ago")]
    private static partial void LogCleanedUp(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(EventId = 405, Level = LogLevel.Warning, Message = "Cleaning up published outbox messages failed; retrying at the next cleanup interval")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 406, Level = LogLevel.Warning, Message = "Publishing an outbox batch was interrupted; recording the confirmed part failed too")]
    private static partial void LogPublishingInterrupted(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 404, Level = LogLevel.Warning, Message = "Closing the outbox publisher channel failed")]
    private static partial void LogChannelCloseFailed(ILogger logger, Exception exception);
}
