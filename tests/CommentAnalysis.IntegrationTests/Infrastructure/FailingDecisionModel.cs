using CommentAnalysis.Core.Models;

namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>
/// A decision model whose first <c>failures</c> evaluations throw (by default an <see cref="InvalidOperationException"/>),
/// simulating a transient or persistent failure.
/// </summary>
internal sealed class FailingDecisionModel(int failures, Func<Exception>? exception = null) : IDecisionModel
{
    private int _calls;

    public ModelInfo Info { get; } = new("failing", "failing@1.0.0");

    public int Calls => Volatile.Read(ref _calls);

    public Task<ModelPrediction> EvaluateAsync(string content, CancellationToken cancellationToken) =>
        Interlocked.Increment(ref _calls) <= failures
            ? throw exception?.Invoke() ?? new InvalidOperationException("Simulated model failure.")
            : Task.FromResult(new ModelPrediction(ContentLabel.Acceptable, 0.97));
}
