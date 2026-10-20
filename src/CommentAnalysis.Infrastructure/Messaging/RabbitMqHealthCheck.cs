using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CommentAnalysis.Infrastructure.Messaging;

/// <summary>Healthy once the topology is declared and while the connection is open.</summary>
public sealed class RabbitMqHealthCheck(RabbitMqConnection connection, RabbitMqTopologyInitializer topology) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = (topology.IsReady, topology.HasConfigurationError, connection.IsOpen) switch
        {
            // Also after startup: the topology is declared again before a lost consumer is recreated.
            (_, true, _) => HealthCheckResult.Unhealthy("RabbitMQ refused the configuration; see the worker logs."),
            (false, false, _) => HealthCheckResult.Unhealthy("RabbitMQ topology has not been declared yet."),
            (true, _, false) => HealthCheckResult.Unhealthy("RabbitMQ connection is closed; waiting for automatic recovery."),
            (true, _, true) => HealthCheckResult.Healthy(),
        };

        return Task.FromResult(result);
    }
}
