using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

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
    private volatile bool _hasConfigurationError;

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    /// <summary>
    /// The last attempt was refused for a reason that retrying will not fix on its own (wrong credentials,
    /// missing permissions, or a queue/exchange declared with different arguments). Retries continue,
    /// because an operator may fix the broker side, but the failure is logged as an error.
    /// </summary>
    public bool HasConfigurationError => _hasConfigurationError;

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

                _hasConfigurationError = false;
                LogTopologyDeclared(logger, settings.Exchange, settings.InputQueue, settings.DeadLetterQueue);
                _ready.TrySetResult();
                return;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _hasConfigurationError = IsConfigurationError(exception);
                if (_hasConfigurationError)
                {
                    LogConfigurationError(logger, exception, attempt, delay);
                }
                else
                {
                    LogAttemptFailed(logger, exception, attempt, delay);
                }
            }

            await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, settings.MaxReconnectDelay.Ticks));
        }
    }

    private static bool IsConfigurationError(Exception exception) => exception switch
    {
        AuthenticationFailureException => true,
        BrokerUnreachableException { InnerException: AuthenticationFailureException } => true,
        OperationInterruptedException { ShutdownReason.ReplyCode: Constants.AccessRefused or Constants.PreconditionFailed } => true,
        _ => false,
    };

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

    [LoggerMessage(
        EventId = 212,
        Level = LogLevel.Error,
        Message = "RabbitMQ refused the worker's configuration (credentials, permissions or queue/exchange arguments) " +
                  "on attempt {Attempt}; this will not resolve without a change. Retrying in {RetryDelay}")]
    private static partial void LogConfigurationError(ILogger logger, Exception exception, int attempt, TimeSpan retryDelay);
}
