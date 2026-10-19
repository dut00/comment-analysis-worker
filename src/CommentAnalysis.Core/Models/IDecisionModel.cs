namespace CommentAnalysis.Core.Models;

/// <summary>
/// A model that evaluates a comment's content and predicts whether it is acceptable.
/// Implementations must be safe to call concurrently.
/// </summary>
public interface IDecisionModel
{
    /// <summary>
    /// The model that answers <see cref="EvaluateAsync"/>. Known before any call, so it can be
    /// used to check whether a comment was already analyzed by this model version.
    /// </summary>
    ModelInfo Info { get; }

    Task<ModelPrediction> EvaluateAsync(string content, CancellationToken cancellationToken);
}
