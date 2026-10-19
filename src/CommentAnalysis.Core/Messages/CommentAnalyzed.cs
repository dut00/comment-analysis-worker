using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;

namespace CommentAnalysis.Core.Messages;

/// <summary>
/// Outgoing event: a comment was analyzed and a moderation decision was made.
/// </summary>
public sealed record CommentAnalyzed
{
    public required Guid CommentId { get; init; }

    public required Guid PostId { get; init; }

    public required Decision Decision { get; init; }

    /// <summary>The model's confidence in its predicted label, within [0, 1].</summary>
    public required double Confidence { get; init; }

    public required string ModelName { get; init; }

    public required string ModelVersion { get; init; }

    public required DateTimeOffset AnalyzedAt { get; init; }

    public static CommentAnalyzed From(CommentCreated comment, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(result);

        return new CommentAnalyzed
        {
            CommentId = comment.CommentId,
            PostId = comment.PostId,
            Decision = result.Decision,
            Confidence = result.Confidence,
            ModelName = result.Model.Name,
            ModelVersion = result.Model.Version,
            AnalyzedAt = result.AnalyzedAt,
        };
    }
}
