using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Decisions;

/// <summary>
/// Turns a model prediction into a moderation decision. A comment is approved or rejected
/// automatically only when the model is confident enough; otherwise it goes to manual review.
/// </summary>
/// <remarks>
/// The prediction's <see cref="ModerationSignals"/> can only stop an approval: a comment the model finds acceptable
/// still goes to a person when it looks toxic, like spam, or like it reveals personal data. They never reject a
/// comment, so a rejection always rests on the label and its confidence.
/// </remarks>
public sealed class DecisionPolicy
{
    private readonly DecisionPolicyOptions _options;

    public DecisionPolicy(DecisionPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = options.GetValidationErrors();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(options));
        }

        _options = options;
    }

    public PolicyDecision Decide(ModelPrediction prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);

        if (prediction.Label == ContentLabel.Unacceptable && prediction.Confidence >= _options.RejectThreshold)
        {
            return new PolicyDecision(Decision.Reject, []);
        }

        var reasons = new List<ReviewReason>();
        var threshold = prediction.Label == ContentLabel.Acceptable ? _options.ApproveThreshold : _options.RejectThreshold;
        if (prediction.Confidence < threshold)
        {
            reasons.Add(ReviewReason.LowConfidence);
        }

        // Reported for an unconfident rejection too, so the reviewer sees everything the model noticed.
        if (prediction.Signals is { } signals)
        {
            AddIfAbove(reasons, ReviewReason.Toxicity, signals.Toxicity, _options.MaxToxicityForApprove);
            AddIfAbove(reasons, ReviewReason.Spam, signals.Spam, _options.MaxSpamForApprove);
            AddIfAbove(reasons, ReviewReason.PersonalData, signals.PersonalData, _options.MaxPersonalDataForApprove);
        }

        return prediction.Label == ContentLabel.Acceptable && reasons.Count == 0
            ? new PolicyDecision(Decision.Approve, [])
            : new PolicyDecision(Decision.ManualReview, reasons);
    }

    private static void AddIfAbove(List<ReviewReason> reasons, ReviewReason reason, double value, double limit)
    {
        if (value > limit)
        {
            reasons.Add(reason);
        }
    }
}
