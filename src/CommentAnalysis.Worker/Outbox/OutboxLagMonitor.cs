using CommentAnalysis.Infrastructure.Persistence;
using CommentAnalysis.Worker.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace CommentAnalysis.Worker.Outbox;

/// <summary>
/// Checks the age of the oldest unpublished outbox message for <see cref="WorkerMetrics"/>. Separate from
/// <see cref="OutboxPublisher"/>, which waits for RabbitMQ: the lag matters most when publishing is stuck.
/// </summary>
internal sealed partial class OutboxLagMonitor(
    IServiceScopeFactory scopeFactory,
    WorkerMetrics metrics,
    TimeProvider timeProvider,
    ILogger<OutboxLagMonitor> logger) : BackgroundService
{
    /// <summary>One indexed query per replica this often; metrics are exported less often anyway.</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    private bool _failing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(CheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CheckAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AnalysisDbContext>();

            // Uses the partial index on unpublished rows.
            var oldest = await context.OutboxMessages
                .Where(m => m.PublishedAt == null && OutboxPublisher.KnownTypes.Contains(m.Type))
                .MinAsync(m => (DateTimeOffset?)m.CreatedAt, stoppingToken)
                .ConfigureAwait(false);
            metrics.OutboxChecked(oldest);
            _failing = false;
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // E.g. the database is unavailable; the lag is unknown until the check works again. The first failure is
            // a warning, because the gauge then has no value and an alert on it stays silent; repeats are not.
            metrics.OutboxCheckFailed();
            if (_failing)
            {
                LogCheckStillFailing(logger, exception);
            }
            else
            {
                LogCheckFailed(logger, exception);
            }

            _failing = true;
        }
    }

    [LoggerMessage(EventId = 410, Level = LogLevel.Warning, Message = "Checking the outbox lag failed; the lag is not reported until it works again")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 411, Level = LogLevel.Debug, Message = "Checking the outbox lag failed again")]
    private static partial void LogCheckStillFailing(ILogger logger, Exception exception);
}
