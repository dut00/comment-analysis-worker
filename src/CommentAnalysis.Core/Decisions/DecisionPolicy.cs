using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Decisions;

/// <summary>
/// Turns a model prediction into a moderation decision. A comment is approved or rejected
/// automatically only when the model is confident enough; otherwise it goes to manual review.
/// </summary>
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

    public Decision Decide(ModelPrediction prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);

        return prediction.Label switch
        {
            ContentLabel.Acceptable when prediction.Confidence >= _options.ApproveThreshold => Decision.Approve,
            ContentLabel.Unacceptable when prediction.Confidence >= _options.RejectThreshold => Decision.Reject,
            _ => Decision.ManualReview,
        };
    }
}
