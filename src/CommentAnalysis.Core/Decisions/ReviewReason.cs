namespace CommentAnalysis.Core.Decisions;

/// <summary>
/// Why a comment was sent to manual review. A comment can have several reasons.
/// </summary>
/// <remarks>The names are stored in the database: do not rename the members.</remarks>
public enum ReviewReason
{
    /// <summary>The model's confidence in its label is below the threshold for that label.</summary>
    LowConfidence,

    /// <summary>The comment is more toxic than <see cref="DecisionPolicyOptions.MaxToxicityForApprove"/>.</summary>
    Toxicity,

    /// <summary>The comment is likelier spam than <see cref="DecisionPolicyOptions.MaxSpamForApprove"/>.</summary>
    Spam,

    /// <summary>The comment likelier reveals personal data than <see cref="DecisionPolicyOptions.MaxPersonalDataForApprove"/>.</summary>
    PersonalData,

    /// <summary>
    /// The comment is longer than <c>Consumer:MaxContentLength</c> (far beyond the expected comment length), so it was
    /// not sent to the model.
    /// </summary>
    TooLong,
}
