using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.Infrastructure.Messaging;

/// <summary>
/// Connects to RabbitMQ and declares the topology in the background, retrying with exponential
/// back-off. The host starts even when RabbitMQ is down; readiness stays unhealthy until this succeeds,
/// and consumers wait for <see cref="WaitUntilReadyAsync"/>.
/// </summary>
public sealed partial class RabbitMqTopologyInitializer(
    RabbitMqConnection connection,
    IOptions<RabbitMqOptions> options,
    TimeProvider timeProvider,
    ILogger<RabbitMqTopologyInitializer> logger) : BackgroundService
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var delay = InitialRetryDelay;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var amqp = await connection.GetAsync(stoppingToken).ConfigureAwait(false);
                var channel = await amqp.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                await using (channel.ConfigureAwait(false))
                {
                    await RabbitMqTopology.DeclareAsync(channel, settings, stoppingToken).ConfigureAwait(false);
                }

                LogTopologyDeclared(logger, settings.Exchange, settings.InputQueue, settings.DeadLetterQueue);
                _ready.TrySetResult();
                return;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogAttemptFailed(logger, exception, attempt, delay);
            }

            await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, settings.MaxReconnectDelay.Ticks));
        }
    }

    [LoggerMessage(
        EventId = 210,
        Level = LogLevel.Information,
        Message = "RabbitMQ topology declared: exchange {Exchange}, queue {InputQueue}, dead-letter queue {DeadLetterQueue}")]
    private static partial void LogTopologyDeclared(ILogger logger, string exchange, string inputQueue, string deadLetterQueue);

    [LoggerMessage(
        EventId = 211,
        Level = LogLevel.Warning,
        Message = "RabbitMQ is not available (attempt {Attempt}); retrying in {RetryDelay}")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, int attempt, TimeSpan retryDelay);
}
