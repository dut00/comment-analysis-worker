using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Models.Llm;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CommentAnalysis.Infrastructure.Models;

/// <summary>
/// Reports whether the decision model can answer. Only a model behind a server (the LLM) is checked; in-process
/// models are always available.
/// </summary>
public sealed class DecisionModelHealthCheck(IDecisionModel model) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        model is LlmDecisionModel llm
            ? llm.CheckHealthAsync(cancellationToken)
            : Task.FromResult(HealthCheckResult.Healthy());
}
