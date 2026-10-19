using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Analysis;

/// <summary>
/// The outcome of analyzing a single comment: the decision, the prediction it was based on,
/// and the model that produced it.
/// </summary>
/// <param name="ReviewReasons">Why the comment needs manual review; empty for other decisions.</param>
/// <param name="Prediction">
/// Null when the model did not evaluate the comment (<see cref="ReviewReason.TooLong"/>).
/// </param>
public sealed record AnalysisResult(
    Decision Decision,
    IReadOnlyList<ReviewReason> ReviewReasons,
    ModelPrediction? Prediction,
    ModelInfo Model,
    DateTimeOffset AnalyzedAt,
    TimeSpan Duration)
{
    /// <summary>The model's confidence in its predicted label; this is the persisted score. Null without a prediction.</summary>
    public double? Confidence => Prediction?.Confidence;
}
