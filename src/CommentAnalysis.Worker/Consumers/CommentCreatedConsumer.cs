using System.Globalization;
using System.Text;
using System.Text.Json;
using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Messages;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace CommentAnalysis.Worker.Consumers;

/// <summary>
/// Consumes <see cref="CommentCreated"/> messages: analyzes and stores each comment, then acks it.
/// A message is acked only after its analysis has been committed, so a crash at any point leads to
/// redelivery, and <see cref="CommentProcessor"/> makes the redelivery harmless.
/// </summary>
/// <remarks>
/// <para>
/// Recovery: after a connection failure the client's automatic recovery restores the channel and the
/// consumer. A channel closed by the broker (e.g. <c>consumer_timeout</c>) or a consumer cancelled by the
/// broker (e.g. the queue was deleted) is not recovered by the client, so this service recreates them,
/// retrying with back-off. <see cref="IsConsuming"/> feeds the readiness check.
/// </para>
/// <para>
/// Shutdown: the consumer is cancelled first (no new deliveries), in-flight messages are allowed to finish
/// and are acked, then the channel is closed. Unacked prefetched messages return to the queue. In-flight work
/// is only cancelled when the host's shutdown timeout expires (see <c>Host:ShutdownTimeout</c>).
/// </para>
/// </remarks>
internal sealed partial class CommentCreatedConsumer(
    RabbitMqConnection connection,
    RabbitMqTopologyInitializer topology,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> rabbitMqOptions,
    IOptions<ConsumerOptions> consumerOptions,
    TimeProvider timeProvider,
    ILogger<CommentCreatedConsumer> logger) : BackgroundService
{
    private static readonly Func<ILogger, Guid, IDisposable?> CommentScope =
        LoggerMessage.DefineScope<Guid>("CommentId: {CommentId}");

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>Caps each step of closing the channel (close, abort, dispose); counted in the shutdown budget.</summary>
    private static readonly TimeSpan ChannelCloseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Longest producer-controlled value (e.g. a message id) written to logs.</summary>
    private const int MaxLoggedValueLength = 128;

    internal const string RetryCountHeader = "x-retry-count";

    private readonly int _concurrency = consumerOptions.Value.Concurrency;
    private readonly int _maxMessageBytes = consumerOptions.Value.MaxMessageBytes;
    private readonly int _maxContentLength = consumerOptions.Value.MaxContentLength;

    // One permit per concurrent handler; shutdown acquires all of them to wait for in-flight messages.
    // A handler from a lost channel may still hold its permit while the recreated channel delivers.
    private readonly SemaphoreSlim _slots = new(consumerOptions.Value.Concurrency, consumerOptions.Value.Concurrency);

    // Cancelled as the first step of shutdown: releases handlers that are waiting for a permit.
    private readonly CancellationTokenSource _stopRequested = new();

    // Cancelled only when the host gives up on graceful shutdown: aborts in-flight processing.
    private readonly CancellationTokenSource _processingCancellation = new();

    // Guards _stopping, _channel and _consumerTag between the recreate loop and shutdown.
    private readonly Lock _stateLock = new();
    private Task? _stopTask;
    private volatile bool _stopping;
    private volatile bool _isConsuming;
    private int _consumerStarts;

    // Back-off while the model is unavailable, shared by all handlers (each failure doubles it); reset when the model
    // answers again.
    private long _modelUnavailableDelayTicks = InitialRetryDelay.Ticks;

    private IChannel? _channel;
    private string? _consumerTag;

    /// <summary>True while a consumer is registered on the input queue (or being restored by connection recovery).</summary>
    public bool IsConsuming => _isConsuming;

    /// <summary>How many times a consumer was registered by this service (connection recovery is not counted).</summary>
    internal int ConsumerStarts => Volatile.Read(ref _consumerStarts);

    /// <summary>The channel currently used for consuming; exposed for tests that need to break it.</summary>
    internal IChannel? CurrentChannel => _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await topology.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);

        var delay = InitialRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await StartConsumingAsync(lost, stoppingToken).ConfigureAwait(false);
                delay = InitialRetryDelay;

                var reason = await lost.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
                _isConsuming = false;
                LogConsumerLost(logger, reason);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _stopping)
            {
                return;
            }
            catch (Exception exception)
            {
                _isConsuming = false;
                LogStartFailed(logger, exception, delay);
            }

            await CloseChannelQuietlyAsync().ConfigureAwait(false);
            await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, rabbitMqOptions.Value.MaxReconnectDelay.Ticks));
        }
    }

    /// <summary>
    /// Idempotent: the host may stop the service more than once (e.g. WebApplicationFactory and
    /// <c>RunAsync</c> both stop it); later calls wait for the first shutdown instead of starting another.
    /// </summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_stopTask is null)
            {
                _stopping = true;
                _stopTask = StopCoreAsync(cancellationToken);
            }

            return _stopTask;
        }
    }

    public override void Dispose()
    {
        _stopRequested.Cancel();
        _processingCancellation.Cancel();
        base.Dispose();

        // The semaphore and the token sources are intentionally not disposed: after a timed-out shutdown,
        // handlers may still be unwinding and use them. None of them holds unmanaged resources here.
    }

    private async Task StartConsumingAsync(TaskCompletionSource<string> lost, CancellationToken stoppingToken)
    {
        // Also cancelled when shutdown begins, so a start in progress does not register a consumer late.
        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _stopRequested.Token);
        var cancellationToken = startCancellation.Token;

        var settings = consumerOptions.Value;
        var amqp = await connection.GetAsync(cancellationToken).ConfigureAwait(false);

        var channel = await amqp.CreateChannelAsync(
            new CreateChannelOptions(
                // Retries are republished on this channel and must be confirmed before the original is acked.
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true,
                consumerDispatchConcurrency: (ushort)settings.Concurrency),
            cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            _channel = channel;
        }

        channel.CallbackExceptionAsync += (_, args) =>
        {
            LogCallbackException(logger, args.Exception);
            return Task.CompletedTask;
        };
        channel.ChannelShutdownAsync += (_, args) =>
        {
            // A connection failure closes the channel too, but automatic recovery restores it with its consumer.
            if (!_stopping && amqp.CloseReason is null)
            {
                lost.TrySetResult($"channel closed by {args.Initiator}: {args.ReplyCode} {args.ReplyText}");
            }

            return Task.CompletedTask;
        };

        await channel.BasicQosAsync(0, (ushort)settings.Prefetch, global: false, cancellationToken).ConfigureAwait(false);

        var consumer = new InputQueueConsumer(channel, onCancelledByBroker: () => lost.TrySetResult("consumer cancelled by the broker"));
        consumer.ReceivedAsync += OnReceivedAsync;

        var queue = rabbitMqOptions.Value.InputQueue;
        var consumerTag = await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);

        bool stopping;
        lock (_stateLock)
        {
            _consumerTag = consumerTag;
            stopping = _stopping;
            if (!stopping)
            {
                _isConsuming = true;
            }
        }

        if (stopping)
        {
            // Shutdown began while the consumer was being registered and may not have seen its tag.
            await CancelConsumerQuietlyAsync(channel, consumerTag, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        Interlocked.Increment(ref _consumerStarts);
        LogConsuming(logger, queue, settings.Concurrency, settings.Prefetch);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        // Release handlers waiting for a permit, and abort a consumer registration in progress.
        await _stopRequested.CancelAsync().ConfigureAwait(false);

        // The host cancels this token when its shutdown timeout expires: abort in-flight work then.
        await using var registration = cancellationToken.Register(_processingCancellation.Cancel).ConfigureAwait(false);

        IChannel? channel;
        string? consumerTag;
        lock (_stateLock)
        {
            (channel, consumerTag) = (_channel, _consumerTag);
        }

        if (channel is { IsOpen: true } && consumerTag is not null)
        {
            await CancelConsumerQuietlyAsync(channel, consumerTag, cancellationToken).ConfigureAwait(false);
        }

        var acquired = 0;
        try
        {
            for (; acquired < _concurrency; acquired++)
            {
                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            LogStopped(logger);
        }
        catch (OperationCanceledException)
        {
            LogShutdownTimedOut(logger, _concurrency - acquired);
        }

        _isConsuming = false;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Unacked messages (prefetched, or aborted by the timeout) are returned to the queue.
        await CloseChannelQuietlyAsync().ConfigureAwait(false);
    }

    private async Task CancelConsumerQuietlyAsync(IChannel channel, string consumerTag, CancellationToken cancellationToken)
    {
        try
        {
            // Capped like the close steps: a broker that never answers must not use up the drain budget.
            await channel.BasicCancelAsync(consumerTag, noWait: false, cancellationToken)
                .WaitAsync(ChannelCloseTimeout, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCancelFailed(logger, exception);
        }
        catch (OperationCanceledException)
        {
            // Shutdown timeout expired; closing the channel removes the consumer anyway.
        }
    }

    private async Task CloseChannelQuietlyAsync()
    {
        IChannel? channel;
        lock (_stateLock)
        {
            (channel, _channel, _consumerTag) = (_channel, null, null);
        }

        if (channel is null)
        {
            return;
        }

        // Every step is capped: the client does not always honour its own timeouts, and disposing a channel
        // waits for handlers still running on it. An abandoned handler cannot ack on a closed channel, and the
        // next channel's handlers wait for its slot, so nothing is processed twice concurrently.
        try
        {
            if (channel.IsOpen)
            {
                await channel.CloseAsync(Constants.ReplySuccess, "Consumer closing", abort: false, CancellationToken.None)
                    .WaitAsync(ChannelCloseTimeout, timeProvider).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // E.g. a half-open connection that never answers: drop the channel without waiting.
            LogChannelCloseFailed(logger, exception);
            await RunCappedAsync(() => channel.CloseAsync(Constants.ReplySuccess, "Consumer closing", abort: true, CancellationToken.None))
                .ConfigureAwait(false);
        }

        await RunCappedAsync(() => channel.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    private async Task RunCappedAsync(Func<Task> step)
    {
        try
        {
            await step().WaitAsync(ChannelCloseTimeout, timeProvider).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogChannelCloseFailed(logger, exception);
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        if (_stopping)
        {
            // Not acked: returns to the queue when the channel closes.
            return;
        }

        // Normally a permit is free (dispatch concurrency equals the permits), but a handler from a lost
        // channel may still hold one; wait for it rather than holding this delivery unacked. Shutdown
        // releases waiting handlers, whose deliveries then return to the queue.
        try
        {
            await _slots.WaitAsync(_stopRequested.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!_stopping)
            {
                var channel = ((AsyncEventingBasicConsumer)sender).Channel;
                await HandleAsync(channel, delivery, _processingCancellation.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        // Invalid input is never retried (the same bytes would fail again); it is dead-lettered for inspection.
        var messageId = SanitizeForLog(delivery.BasicProperties.MessageId);
        if (delivery.Body.Length > _maxMessageBytes)
        {
            LogMessageTooLarge(logger, messageId, delivery.DeliveryTag, delivery.Body.Length, _maxMessageBytes);
            await DeadLetterAsync(channel, delivery).ConfigureAwait(false);
            return;
        }

        CommentCreated comment;
        try
        {
            comment = MessageSerializer.Deserialize<CommentCreated>(delivery.Body.Span);
        }
        catch (JsonException exception)
        {
            LogMalformedMessage(logger, exception, messageId, delivery.DeliveryTag);
            await DeadLetterAsync(channel, delivery).ConfigureAwait(false);
            return;
        }

        using var scope = CommentScope(logger, comment.CommentId);

        ProcessingResult result;
        try
        {
            await using var services = scopeFactory.CreateAsyncScope();
            var processor = services.ServiceProvider.GetRequiredService<CommentProcessor>();
            result = await processor.ProcessAsync(comment, _maxContentLength, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown timeout expired mid-processing; the message is redelivered after the channel closes.
            LogProcessingAborted(logger);
            return;
        }
        catch (ModelUnavailableException exception)
        {
            await RequeueWhileModelUnavailableAsync(channel, delivery, exception).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            // E.g. the database is unavailable, or the model gave an invalid answer.
            await RetryOrDeadLetterAsync(channel, delivery, exception).ConfigureAwait(false);
            return;
        }

        if (result.Analysis is { Prediction: not null })
        {
            // The model answered (a duplicate or a comment too long is stored without asking it).
            Interlocked.Exchange(ref _modelUnavailableDelayTicks, InitialRetryDelay.Ticks);
        }

        // Not cancellable: the analysis is committed, so a lost ack only causes a harmless redelivery.
        await SettleAsync(() => channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None))
            .ConfigureAwait(false);

        if (result.Analysis is { Prediction: null })
        {
            // Not evaluated: the processor stored a manual review instead of occupying the model for too long.
            LogContentTooLong(logger, comment.Content.Length, _maxContentLength);
        }
        else if (result.Analysis is { } analysis)
        {
            LogAnalyzed(logger, analysis.Decision, analysis.Confidence, analysis.Model.Version, analysis.Duration.TotalMilliseconds);
        }
        else
        {
            LogAlreadyAnalyzed(logger, result.Model.Version);
        }
    }

    /// <summary>
    /// Returns a message to the queue, uncounted, after a back-off (1 s doubling to
    /// <see cref="ConsumerOptions.MaxRetryDelay"/>). The comment is not at fault, so it must not use up its retries or
    /// reach the DLQ: while the model is down the worker effectively pauses, and readiness reports the model.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="RetryOrDeadLetterAsync"/>, an endless loop is intended here: the messages wait for the model.
    /// A nack with requeue does not count towards <c>x-delivery-limit</c> in RabbitMQ 4.
    /// </remarks>
    private async Task RequeueWhileModelUnavailableAsync(IChannel channel, BasicDeliverEventArgs delivery, ModelUnavailableException exception)
    {
        // Doubled atomically, so concurrent handlers do not lose a reset made by a success in between.
        long delayTicks;
        do
        {
            delayTicks = Interlocked.Read(ref _modelUnavailableDelayTicks);
        }
        while (Interlocked.CompareExchange(
            ref _modelUnavailableDelayTicks, Math.Min(delayTicks * 2, ConsumerOptions.MaxRetryDelay.Ticks), delayTicks) != delayTicks);

        var delay = TimeSpan.FromTicks(delayTicks);
        LogModelUnavailable(logger, exception, delay);

        try
        {
            await Task.Delay(delay, timeProvider, _stopRequested.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown: the message returns to the queue when the channel closes.
            return;
        }

        await SettleAsync(() => channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Retries a failed message by republishing a copy with an incremented <c>x-retry-count</c> header and then
    /// acking the original, or dead-letters it once <see cref="ConsumerOptions.MaxRetries"/> is reached.
    /// </summary>
    /// <remarks>
    /// A nack with requeue would not do: RabbitMQ 4 does not count it towards <c>x-delivery-limit</c>, so a
    /// message that always fails would loop forever. The copy goes through the default exchange straight to the
    /// input queue, so other subscribers of <c>comment.created</c> do not see it. If the worker crashes between
    /// the confirmed publish and the ack (or the ack fails on a lost channel), both copies are processed, which
    /// idempotency makes harmless; under a persistent failure each copy may end up in the DLQ.
    /// </remarks>
    private async Task RetryOrDeadLetterAsync(IChannel channel, BasicDeliverEventArgs delivery, Exception exception)
    {
        var retryCount = GetRetryCount(delivery.BasicProperties);
        var settings = consumerOptions.Value;
        if (retryCount >= settings.MaxRetries)
        {
            LogRetriesExhausted(logger, exception, retryCount);
            await DeadLetterAsync(channel, delivery).ConfigureAwait(false);
            return;
        }

        LogProcessingFailed(logger, exception, retryCount + 1, settings.MaxRetries);
        try
        {
            // Shutdown interrupts the delay; the message is then redelivered with the same retry count.
            var delay = TimeSpan.FromTicks(Math.Min(settings.RetryDelay.Ticks * (retryCount + 1), ConsumerOptions.MaxRetryDelay.Ticks));
            await Task.Delay(delay, timeProvider, _stopRequested.Token).ConfigureAwait(false);

            var properties = new BasicProperties(delivery.BasicProperties)
            {
                Headers = CreateRetryHeaders(delivery.BasicProperties.Headers, retryCount + 1),
            };

            // Waiting for the confirm is cancelled only by the shutdown timeout (e.g. a blocked connection).
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: rabbitMqOptions.Value.InputQueue,
                mandatory: true,
                properties,
                delivery.Body,
                _processingCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopRequested.IsCancellationRequested)
        {
            return;
        }
        catch (Exception publishException)
        {
            // Not confirmed (or returned as unroutable): keep the original instead, uncounted.
            LogRetryPublishFailed(logger, publishException);
            await SettleAsync(() => channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None))
                .ConfigureAwait(false);
            return;
        }

        await SettleAsync(() => channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Headers of a retry copy: the original ones with the incremented retry count, minus those that must not travel
    /// with a new message. <c>CC</c>/<c>BCC</c> would make RabbitMQ also route the copy to the queues they name (with
    /// the worker's permissions), and the dead-lettering and delivery-count headers describe the original message.
    /// </summary>
    internal static Dictionary<string, object?> CreateRetryHeaders(IDictionary<string, object?>? original, int retryCount)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in original ?? new Dictionary<string, object?>())
        {
            if (!IsDroppedOnRetry(key))
            {
                headers[key] = value;
            }
        }

        headers[RetryCountHeader] = retryCount;
        return headers;
    }

    private static bool IsDroppedOnRetry(string header) =>
        header is "CC" or "BCC" or "x-death" or "x-delivery-count"
        || header.StartsWith("x-first-death-", StringComparison.Ordinal)
        || header.StartsWith("x-last-death-", StringComparison.Ordinal);

    /// <summary>Reads the <c>x-retry-count</c> header; a missing, malformed or negative value counts as zero.</summary>
    internal static int GetRetryCount(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is null || !properties.Headers.TryGetValue(RetryCountHeader, out var value))
        {
            return 0;
        }

        long count = value switch
        {
            int number => number,
            long number => number,
            short number => number,
            byte number => number,
            sbyte number => number,
            uint number => number,
            byte[] text when long.TryParse(Encoding.UTF8.GetString(text), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };

        return (int)Math.Clamp(count, 0, int.MaxValue);
    }

    private Task DeadLetterAsync(IChannel channel, BasicDeliverEventArgs delivery) =>
        SettleAsync(() => channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None));

    /// <summary>
    /// Makes a producer-controlled value safe to log: truncated, with control characters (e.g. CR/LF, which
    /// would allow forging log lines in plain-text formatters) replaced.
    /// </summary>
    internal static string? SanitizeForLog(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var length = Math.Min(value.Length, MaxLoggedValueLength);
        return string.Create(length, value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
    }

    /// <summary>
    /// Acks, nacks or rejects a delivery. If the channel is already closed the broker requeues the message
    /// by itself, so the failure is logged rather than thrown into the client's dispatcher.
    /// </summary>
    private async Task SettleAsync(Func<ValueTask> settle)
    {
        try
        {
            await settle().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AlreadyClosedException or OperationInterruptedException or ObjectDisposedException)
        {
            LogSettleFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Consuming {Queue} (concurrency {Concurrency}, prefetch {Prefetch})")]
    private static partial void LogConsuming(ILogger logger, string queue, int concurrency, int prefetch);

    [LoggerMessage(EventId = 301, Level = LogLevel.Information, Message = "Comment analyzed: {Decision} with confidence {Confidence} by {ModelVersion} in {DurationMs} ms")]
    private static partial void LogAnalyzed(ILogger logger, Core.Decisions.Decision decision, double? confidence, string modelVersion, double durationMs);

    [LoggerMessage(EventId = 302, Level = LogLevel.Information, Message = "Comment already analyzed by {ModelVersion}; acknowledged without reprocessing")]
    private static partial void LogAlreadyAnalyzed(ILogger logger, string modelVersion);

    [LoggerMessage(EventId = 303, Level = LogLevel.Error, Message = "Rejected malformed message {MessageId} (delivery {DeliveryTag}) to the dead-letter queue")]
    private static partial void LogMalformedMessage(ILogger logger, Exception exception, string? messageId, ulong deliveryTag);

    [LoggerMessage(EventId = 304, Level = LogLevel.Warning, Message = "Processing the comment failed; scheduling retry {Retry} of {MaxRetries}")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, int retry, int maxRetries);

    [LoggerMessage(EventId = 318, Level = LogLevel.Warning, Message = "The model is unavailable; returning the message to the queue in {Delay}")]
    private static partial void LogModelUnavailable(ILogger logger, Exception exception, TimeSpan delay);

    [LoggerMessage(EventId = 316, Level = LogLevel.Error, Message = "Processing the comment failed after {Retries} retries; rejected to the dead-letter queue")]
    private static partial void LogRetriesExhausted(ILogger logger, Exception exception, int retries);

    [LoggerMessage(EventId = 317, Level = LogLevel.Error, Message = "Republishing the message for a retry failed; the original is requeued")]
    private static partial void LogRetryPublishFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 305, Level = LogLevel.Warning, Message = "Processing was aborted by the shutdown timeout; the message will be redelivered")]
    private static partial void LogProcessingAborted(ILogger logger);

    [LoggerMessage(EventId = 306, Level = LogLevel.Warning, Message = "Cancelling the RabbitMQ consumer failed")]
    private static partial void LogCancelFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 307, Level = LogLevel.Information, Message = "Consumer stopped; in-flight messages completed")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(EventId = 308, Level = LogLevel.Warning, Message = "Shutdown timeout expired with {InFlight} message(s) still in flight; they will be redelivered")]
    private static partial void LogShutdownTimedOut(ILogger logger, int inFlight);

    [LoggerMessage(EventId = 309, Level = LogLevel.Error, Message = "Stopped consuming: {Reason}. Recreating the consumer")]
    private static partial void LogConsumerLost(ILogger logger, string reason);

    [LoggerMessage(EventId = 310, Level = LogLevel.Error, Message = "Starting the consumer failed; retrying in {RetryDelay}")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, TimeSpan retryDelay);

    [LoggerMessage(EventId = 311, Level = LogLevel.Warning, Message = "Settling the delivery failed because the channel is closed; RabbitMQ will redeliver it")]
    private static partial void LogSettleFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 312, Level = LogLevel.Error, Message = "Unhandled exception in a RabbitMQ consumer callback")]
    private static partial void LogCallbackException(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 314, Level = LogLevel.Error, Message = "Rejected message {MessageId} (delivery {DeliveryTag}) to the dead-letter queue: body of {Length} bytes exceeds the limit of {Limit}")]
    private static partial void LogMessageTooLarge(ILogger logger, string? messageId, ulong deliveryTag, int length, int limit);

    [LoggerMessage(EventId = 315, Level = LogLevel.Warning, Message = "Sent the comment to manual review without evaluating it: content of {Length} characters exceeds the limit of {Limit}")]
    private static partial void LogContentTooLong(ILogger logger, int length, int limit);

    [LoggerMessage(EventId = 313, Level = LogLevel.Warning, Message = "Closing the consumer channel failed")]
    private static partial void LogChannelCloseFailed(ILogger logger, Exception exception);
}
