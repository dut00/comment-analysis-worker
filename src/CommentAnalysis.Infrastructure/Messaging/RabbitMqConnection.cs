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
            await _connection.CloseAsync().ConfigureAwait(false);
            _connection.Dispose();
        }

        _lock.Dispose();
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Connected to RabbitMQ at {Host}:{Port}")]
    private static partial void LogConnected(ILogger logger, string host, int port);
}
