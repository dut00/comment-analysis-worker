using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Tests.Fakes;

/// <summary>
/// A decision model that returns a preconfigured prediction and records what it was asked.
/// </summary>
internal sealed class FakeDecisionModel : IDecisionModel
{
    private readonly Func<string, ModelPrediction> _predict;

    public FakeDecisionModel(ModelPrediction prediction, ModelInfo? info = null)
        : this(_ => prediction, info)
    {
    }

    public FakeDecisionModel(Func<string, ModelPrediction> predict, ModelInfo? info = null)
    {
        _predict = predict;
        Info = info ?? new ModelInfo("fake", "fake@1.0.0");
    }

    public ModelInfo Info { get; }

    public List<string> EvaluatedContents { get; } = [];

    /// <summary>Invoked before returning the prediction, e.g. to advance a fake clock.</summary>
    public Action? OnEvaluate { get; init; }

    public Task<ModelPrediction> EvaluateAsync(string content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EvaluatedContents.Add(content);
        OnEvaluate?.Invoke();

        return Task.FromResult(_predict(content));
    }
}
