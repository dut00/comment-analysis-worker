using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Infrastructure.Models;

/// <summary>
/// Returns the same configured prediction for every comment, so the worker can run
/// end to end without a real model. Not intended for production.
/// </summary>
public sealed class StaticDecisionModel : IDecisionModel
{
    private readonly ModelPrediction _prediction;

    public StaticDecisionModel(StaticModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Info = new ModelInfo(options.Name, $"{options.Name}@{options.Version}");
        _prediction = new ModelPrediction(options.Label, options.Confidence);
    }

    public ModelInfo Info { get; }

    public Task<ModelPrediction> EvaluateAsync(string content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_prediction);
    }
}
