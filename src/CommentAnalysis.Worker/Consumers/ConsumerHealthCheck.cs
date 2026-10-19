using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CommentAnalysis.Worker.Consumers;

/// <summary>Unhealthy while no consumer is registered on the input queue (starting, or being recreated).</summary>
internal sealed class ConsumerHealthCheck(CommentCreatedConsumer consumer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(consumer.IsConsuming
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Not consuming from the input queue."));
}
