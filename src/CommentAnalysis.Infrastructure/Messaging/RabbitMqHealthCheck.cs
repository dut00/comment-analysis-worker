using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CommentAnalysis.Infrastructure.Messaging;

/// <summary>Healthy once the topology is declared and while the connection is open.</summary>
public sealed class RabbitMqHealthCheck(RabbitMqConnection connection, RabbitMqTopologyInitializer topology) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = (topology.IsReady, connection.IsOpen) switch
        {
            (false, _) => HealthCheckResult.Unhealthy("RabbitMQ topology has not been declared yet."),
            (true, false) => HealthCheckResult.Unhealthy("RabbitMQ connection is closed; waiting for automatic recovery."),
            (true, true) => HealthCheckResult.Healthy(),
        };

        return Task.FromResult(result);
    }
}
