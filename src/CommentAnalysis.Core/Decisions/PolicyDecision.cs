namespace CommentAnalysis.Core.Decisions;

/// <summary>
/// The outcome of <see cref="DecisionPolicy.Decide"/>: the decision and, for manual review, why.
/// </summary>
/// <param name="Decision">The moderation decision.</param>
/// <param name="ReviewReasons">Empty unless <paramref name="Decision"/> is <see cref="Decision.ManualReview"/>.</param>
public sealed record PolicyDecision(Decision Decision, IReadOnlyList<ReviewReason> ReviewReasons);
