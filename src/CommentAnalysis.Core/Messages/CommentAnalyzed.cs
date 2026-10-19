using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;

namespace CommentAnalysis.Core.Messages;

/// <summary>
/// Outgoing event: a comment was analyzed and a moderation decision was made.
/// </summary>
public sealed record CommentAnalyzed
{
    /// <summary>The stable type name of this message (e.g. in the outbox); must not change with the class name.</summary>
    public const string MessageType = "CommentAnalyzed";

    public required Guid CommentId { get; init; }

    public required Guid PostId { get; init; }

    public required Decision Decision { get; init; }

    /// <summary>
    /// The model's confidence in its predicted label, within [0, 1]; null when the model did not evaluate the comment
    /// (a manual review because it is too long).
    /// </summary>
    public required double? Confidence { get; init; }

    public required string ModelName { get; init; }

    public required string ModelVersion { get; init; }

    public required DateTimeOffset AnalyzedAt { get; init; }

    public static CommentAnalyzed From(CommentCreated comment, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(comment);

        return From(comment.CommentId, comment.PostId, result);
    }

    public static CommentAnalyzed From(Guid commentId, Guid postId, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CommentAnalyzed
        {
            CommentId = commentId,
            PostId = postId,
            Decision = result.Decision,
            Confidence = result.Confidence,
            ModelName = result.Model.Name,
            ModelVersion = result.Model.Version,
            AnalyzedAt = result.AnalyzedAt,
        };
    }
}
