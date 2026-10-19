using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CommentAnalysis.Infrastructure.Messaging;

/// <summary>
/// A named RabbitMQ connection, opened on first use; afterwards the client's automatic recovery reconnects it
/// (and re-creates channels and consumers) after outages.
/// </summary>
/// <remarks>
/// The worker uses two: <see cref="ConsumerName"/> (topology, consuming and acks, resolved without a key) and
/// <see cref="PublisherName"/> (keyed). RabbitMQ blocks publishing connections under flow control or resource
/// alarms, which must not stall the acks of the consumer.
/// </remarks>
public sealed partial class RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger, string name)
    : IAsyncDisposable
{
    public const string ConsumerName = "consumer";
    public const string PublisherName = "publisher";

    /// <summary>Bounds shutdown when the broker is unreachable; the close handshake otherwise has no limit.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public string Name => name;

    public bool IsOpen => _connection?.IsOpen == true;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { } existing)
        {
            return existing;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is null)
            {
                var settings = options.Value;
                var factory = new ConnectionFactory
                {
                    Uri = settings.Uri!,
                    ClientProvidedName = $"{settings.ClientName}:{name}",
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                };

                _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
                LogConnected(logger, name, settings.Uri!.Host, settings.Uri.Port);
            }

            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            // The client does not always honour its own close timeout (observed: a close hanging for 30 s),
            // so each step is also capped here. Shutdown must never wait on the broker beyond this budget.
            try
            {
                await _connection.CloseAsync(CloseTimeout).WaitAsync(CloseTimeout).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogCloseFailed(logger, name, exception);
                try
                {
                    await _connection.AbortAsync(CloseTimeout).WaitAsync(CloseTimeout).ConfigureAwait(false);
                }
                catch (Exception abortException)
                {
                    LogAbortFailed(logger, name, abortException);
                }
            }

            // Disposing a connection that is still open would block on closing it again; the process is
            // exiting anyway, so leave it to the OS in that case (in tests this leaks one socket per such host).
            if (!_connection.IsOpen)
            {
                _connection.Dispose();
            }
        }

        _lock.Dispose();
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Opened the {Connection} connection to RabbitMQ at {Host}:{Port}")]
    private static partial void LogConnected(ILogger logger, string connection, string host, int port);

    [LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "Closing the {Connection} RabbitMQ connection failed; aborting it")]
    private static partial void LogCloseFailed(ILogger logger, string connection, Exception exception);

    [LoggerMessage(EventId = 202, Level = LogLevel.Warning, Message = "Aborting the {Connection} RabbitMQ connection did not complete in time; abandoning it")]
    private static partial void LogAbortFailed(ILogger logger, string connection, Exception exception);
}
