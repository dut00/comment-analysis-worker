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
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

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
            try
            {
                await _connection.CloseAsync(CloseTimeout).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Shutdown must not hang or fail because the broker is gone; drop the connection instead.
                LogCloseFailed(logger, exception);
                await _connection.AbortAsync(CloseTimeout).ConfigureAwait(false);
            }

            _connection.Dispose();
        }

        _lock.Dispose();
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Connected to RabbitMQ at {Host}:{Port}")]
    private static partial void LogConnected(ILogger logger, string host, int port);

    [LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "Closing the RabbitMQ connection failed; aborting it")]
    private static partial void LogCloseFailed(ILogger logger, Exception exception);
}
