namespace CommentAnalysis.Core.Models;

/// <summary>
/// The label a decision model assigns to a comment, independent of the concrete model
/// (e.g. "non-toxic" from a toxicity classifier or "approve" from Jev map to <see cref="Acceptable"/>).
/// </summary>
public enum ContentLabel
{
    Acceptable,
    Unacceptable,
}
