namespace CommentAnalysis.Core.Decisions;

/// <summary>
/// The final moderation outcome for a comment.
/// </summary>
public enum Decision
{
    Approve,
    Reject,
    ManualReview,
}
