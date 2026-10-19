using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Analysis;

/// <summary>
/// The outcome of analyzing a single comment: the decision, the prediction it was based on,
/// and the model that produced it.
/// </summary>
public sealed record AnalysisResult(
    Decision Decision,
    ModelPrediction Prediction,
    ModelInfo Model,
    DateTimeOffset AnalyzedAt,
    TimeSpan Duration)
{
    /// <summary>The model's confidence in its predicted label; this is the persisted score.</summary>
    public double Confidence => Prediction.Confidence;
}
