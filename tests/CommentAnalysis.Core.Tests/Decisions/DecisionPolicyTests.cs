using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Tests.Decisions;

public class DecisionPolicyTests
{
    private readonly DecisionPolicy _policy = new(new DecisionPolicyOptions
    {
        ApproveThreshold = 0.85,
        RejectThreshold = 0.90,
    });

    [Theory]
    [InlineData(0.85)]
    [InlineData(0.99)]
    [InlineData(1.0)]
    public void Decide_ReturnsApprove_WhenAcceptableAndConfidenceAtOrAboveThreshold(double confidence)
    {
        var decision = _policy.Decide(new ModelPrediction(ContentLabel.Acceptable, confidence));

        Assert.Equal(Decision.Approve, decision);
    }

    [Theory]
    [InlineData(0.90)]
    [InlineData(0.95)]
    [InlineData(1.0)]
    public void Decide_ReturnsReject_WhenUnacceptableAndConfidenceAtOrAboveThreshold(double confidence)
    {
        var decision = _policy.Decide(new ModelPrediction(ContentLabel.Unacceptable, confidence));

        Assert.Equal(Decision.Reject, decision);
    }

    [Theory]
    [InlineData(ContentLabel.Acceptable, 0.849)]
    [InlineData(ContentLabel.Acceptable, 0.5)]
    [InlineData(ContentLabel.Unacceptable, 0.899)]
    [InlineData(ContentLabel.Unacceptable, 0.85)]
    [InlineData(ContentLabel.Unacceptable, 0.5)]
    public void Decide_ReturnsManualReview_WhenConfidenceBelowThreshold(ContentLabel label, double confidence)
    {
        var decision = _policy.Decide(new ModelPrediction(label, confidence));

        Assert.Equal(Decision.ManualReview, decision);
    }

    [Fact]
    public void Decide_UsesDefaultThresholds_WhenOptionsNotCustomized()
    {
        var policy = new DecisionPolicy(new DecisionPolicyOptions());

        Assert.Equal(Decision.Approve, policy.Decide(new ModelPrediction(ContentLabel.Acceptable, 0.85)));
        Assert.Equal(Decision.ManualReview, policy.Decide(new ModelPrediction(ContentLabel.Unacceptable, 0.89)));
        Assert.Equal(Decision.Reject, policy.Decide(new ModelPrediction(ContentLabel.Unacceptable, 0.90)));
    }

    [Fact]
    public void Decide_Throws_WhenPredictionIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => _policy.Decide(null!));
    }

    [Theory]
    [InlineData(0.49, 0.9)]
    [InlineData(1.01, 0.9)]
    [InlineData(0.85, 0.49)]
    [InlineData(0.85, 1.01)]
    [InlineData(double.NaN, 0.9)]
    public void Constructor_Throws_WhenThresholdIsOutOfRange(double approveThreshold, double rejectThreshold)
    {
        var options = new DecisionPolicyOptions
        {
            ApproveThreshold = approveThreshold,
            RejectThreshold = rejectThreshold,
        };

        Assert.Throws<ArgumentException>(() => new DecisionPolicy(options));
    }

    [Fact]
    public void Constructor_Throws_WhenOptionsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DecisionPolicy(null!));
    }
}
