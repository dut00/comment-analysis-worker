using CommentAnalysis.Core.Models;

namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>A decision model whose first evaluation throws, simulating a transient failure.</summary>
internal sealed class FailingOnceDecisionModel : IDecisionModel
{
    private int _calls;

    public ModelInfo Info { get; } = new("failing-once", "failing-once@1.0.0");

    public int Calls => Volatile.Read(ref _calls);

    public Task<ModelPrediction> EvaluateAsync(string content, CancellationToken cancellationToken) =>
        Interlocked.Increment(ref _calls) == 1
            ? throw new InvalidOperationException("Simulated transient model failure.")
            : Task.FromResult(new ModelPrediction(ContentLabel.Acceptable, 0.97));
}
