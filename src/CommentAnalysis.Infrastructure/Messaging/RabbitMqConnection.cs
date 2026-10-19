using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CommentAnalysis.Infrastructure.Messaging;

/// <summary>
/// The worker's single, shared RabbitMQ connection. It is opened on first use; afterwards the
/// client's automatic recovery reconnects it (and re-creates channels and consumers) after outages.
/// </summary>
public sealed partial class RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger)
    : IAsyncDisposable
{
    /// <summary>Bounds shutdown when the broker is unreachable; the close handshake otherwise has no limit.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

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
                    ClientProvidedName = settings.ClientName,
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                };

                _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
                LogConnected(logger, settings.Uri!.Host, settings.Uri.Port);
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
                LogCloseFailed(logger, exception);
                try
                {
                    await _connection.AbortAsync(CloseTimeout).WaitAsync(CloseTimeout).ConfigureAwait(false);
                }
                catch (Exception abortException)
                {
                    LogAbortFailed(logger, abortException);
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

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Connected to RabbitMQ at {Host}:{Port}")]
    private static partial void LogConnected(ILogger logger, string host, int port);

    [LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "Closing the RabbitMQ connection failed; aborting it")]
    private static partial void LogCloseFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 202, Level = LogLevel.Warning, Message = "Aborting the RabbitMQ connection did not complete in time; abandoning it")]
    private static partial void LogAbortFailed(ILogger logger, Exception exception);
}
