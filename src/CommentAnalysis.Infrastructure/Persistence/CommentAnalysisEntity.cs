using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Infrastructure.Persistence;

/// <summary>
/// One analysis of a comment by a specific model version. A comment can have several rows,
/// one per model version that analyzed it.
/// </summary>
public sealed class CommentAnalysisEntity
{
    public const string UniqueCommentModelVersionIndex = "ux_comment_analyses_comment_id_model_version";

    public Guid Id { get; set; }

    public Guid CommentId { get; set; }

    public Guid PostId { get; set; }

    public Decision Decision { get; set; }

    public ContentLabel Label { get; set; }

    /// <summary>The model's confidence in <see cref="Label"/> – the persisted score.</summary>
    public double Confidence { get; set; }

    /// <summary>Model-specific raw probabilities, stored as jsonb for diagnostics.</summary>
    public Dictionary<string, double> Probabilities { get; set; } = [];

    /// <summary>
    /// Why the comment needs manual review (<see cref="ReviewReason"/> names); empty for other decisions.
    /// </summary>
    public List<string> ReviewReasons { get; set; } = [];

    /// <summary>The <see cref="ModerationSignals"/>, or null when the model does not provide them.</summary>
    public double? Toxicity { get; set; }

    public double? SpamProbability { get; set; }

    public double? PersonalDataProbability { get; set; }

    public required string ModelName { get; set; }

    public required string ModelVersion { get; set; }

    public DateTimeOffset AnalyzedAt { get; set; }

    public int DurationMs { get; set; }

    public static CommentAnalysisEntity Create(Guid commentId, Guid postId, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CommentAnalysisEntity
        {
            Id = Guid.CreateVersion7(result.AnalyzedAt),
            CommentId = commentId,
            PostId = postId,
            Decision = result.Decision,
            Label = result.Prediction.Label,
            Confidence = result.Confidence,
            Probabilities = new Dictionary<string, double>(result.Prediction.Probabilities),
            ReviewReasons = [.. result.ReviewReasons.Select(reason => reason.ToString())],
            Toxicity = result.Prediction.Signals?.Toxicity,
            SpamProbability = result.Prediction.Signals?.Spam,
            PersonalDataProbability = result.Prediction.Signals?.PersonalData,
            ModelName = result.Model.Name,
            ModelVersion = result.Model.Version,
            AnalyzedAt = result.AnalyzedAt,
            DurationMs = (int)Math.Round(result.Duration.TotalMilliseconds),
        };
    }
}
