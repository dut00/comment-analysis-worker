namespace CommentAnalysis.Core.Decisions;

/// <summary>
/// Confidence thresholds that a prediction must reach to be decided automatically.
/// Anything below the relevant threshold is sent to manual review.
/// </summary>
public sealed class DecisionPolicyOptions
{
    public const string SectionName = "DecisionPolicy";

    /// <summary>
    /// Thresholds below 0.5 are rejected: for a binary label the chosen label always has
    /// a confidence of at least 0.5, so a lower threshold would never route anything to review.
    /// </summary>
    public const double MinThreshold = 0.5;

    public const double MaxThreshold = 1.0;

    /// <summary>Minimum confidence to approve an <c>Acceptable</c> prediction.</summary>
    public double ApproveThreshold { get; set; } = 0.85;

    /// <summary>
    /// Minimum confidence to reject an <c>Unacceptable</c> prediction. 0.85 was chosen from the quality report
    /// (docs/quality): at 0.90 most insults, threats and spam went to manual review at 0.87–0.90.
    /// </summary>
    public double RejectThreshold { get; set; } = 0.85;

    /// <summary>
    /// The highest <see cref="Models.ModerationSignals.Toxicity"/> that still allows an approval. The default lets
    /// through what is at most around "mild" (rude or sarcastic) on a four-level scale (0, 1/3, 2/3, 1).
    /// </summary>
    public double MaxToxicityForApprove { get; set; } = 0.35;

    /// <summary>The highest <see cref="Models.ModerationSignals.Spam"/> probability that still allows an approval.</summary>
    public double MaxSpamForApprove { get; set; } = 0.5;

    /// <summary>The highest <see cref="Models.ModerationSignals.PersonalData"/> probability that still allows an approval.</summary>
    public double MaxPersonalDataForApprove { get; set; } = 0.5;

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        AddErrorIfOutOfRange(errors, nameof(ApproveThreshold), ApproveThreshold, MinThreshold);
        AddErrorIfOutOfRange(errors, nameof(RejectThreshold), RejectThreshold, MinThreshold);
        AddErrorIfOutOfRange(errors, nameof(MaxToxicityForApprove), MaxToxicityForApprove, 0);
        AddErrorIfOutOfRange(errors, nameof(MaxSpamForApprove), MaxSpamForApprove, 0);
        AddErrorIfOutOfRange(errors, nameof(MaxPersonalDataForApprove), MaxPersonalDataForApprove, 0);

        return errors;
    }

    private static void AddErrorIfOutOfRange(List<string> errors, string name, double value, double min)
    {
        if (!double.IsFinite(value) || value < min || value > MaxThreshold)
        {
            errors.Add($"{SectionName}:{name} must be within [{min}, {MaxThreshold}], but was {value}.");
        }
    }
}
