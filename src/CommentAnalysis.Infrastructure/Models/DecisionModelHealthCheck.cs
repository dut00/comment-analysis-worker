using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Models.SystemOne;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CommentAnalysis.Infrastructure.Models;

/// <summary>
/// Reports whether the decision model can answer. Only a model behind a server (<c>/v1/systemone</c>) is checked; in-process
/// models are always available.
/// </summary>
public sealed class DecisionModelHealthCheck(IDecisionModel model) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        model is SystemOneDecisionModel systemOne
            ? systemOne.CheckHealthAsync(cancellationToken)
            : Task.FromResult(HealthCheckResult.Healthy());
}
