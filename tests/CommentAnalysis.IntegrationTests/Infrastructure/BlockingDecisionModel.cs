using CommentAnalysis.Core.Models;

namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>
/// A decision model whose evaluations block until the test releases them, to observe the worker
/// while messages are in flight (e.g. during shutdown).
/// </summary>
internal sealed class BlockingDecisionModel : IDecisionModel
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    public ModelInfo Info { get; } = new("blocking", "blocking@1.0.0");

    /// <summary>Number of evaluations that have started (and are blocked until <see cref="Release"/>).</summary>
    public int StartedCount => Volatile.Read(ref _started);

    public void Release() => _release.TrySetResult();

    public async Task<ModelPrediction> EvaluateAsync(string content, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _started);
        await _release.Task.WaitAsync(cancellationToken);

        return new ModelPrediction(ContentLabel.Acceptable, 0.97);
    }
}
