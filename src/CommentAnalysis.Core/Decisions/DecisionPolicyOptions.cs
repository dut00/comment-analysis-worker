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

    /// <summary>Minimum confidence to reject an <c>Unacceptable</c> prediction.</summary>
    public double RejectThreshold { get; set; } = 0.90;

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        AddErrorIfOutOfRange(errors, nameof(ApproveThreshold), ApproveThreshold);
        AddErrorIfOutOfRange(errors, nameof(RejectThreshold), RejectThreshold);

        return errors;
    }

    private static void AddErrorIfOutOfRange(List<string> errors, string name, double value)
    {
        if (!double.IsFinite(value) || value is < MinThreshold or > MaxThreshold)
        {
            errors.Add($"{SectionName}:{name} must be within [{MinThreshold}, {MaxThreshold}], but was {value}.");
        }
    }
}
