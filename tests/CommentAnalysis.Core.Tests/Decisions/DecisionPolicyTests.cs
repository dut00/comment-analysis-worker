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
        var decision = _policy.Decide(new ModelPrediction(ContentLabel.Acceptable, confidence)).Decision;

        Assert.Equal(Decision.Approve, decision);
    }

    [Theory]
    [InlineData(0.90)]
    [InlineData(0.95)]
    [InlineData(1.0)]
    public void Decide_ReturnsReject_WhenUnacceptableAndConfidenceAtOrAboveThreshold(double confidence)
    {
        var decision = _policy.Decide(new ModelPrediction(ContentLabel.Unacceptable, confidence)).Decision;

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

        Assert.Equal(Decision.ManualReview, decision.Decision);
        Assert.Equal([ReviewReason.LowConfidence], decision.ReviewReasons);
    }

    [Fact]
    public void Decide_UsesDefaultThresholds_WhenOptionsNotCustomized()
    {
        var policy = new DecisionPolicy(new DecisionPolicyOptions());

        Assert.Equal(Decision.Approve, policy.Decide(new ModelPrediction(ContentLabel.Acceptable, 0.85)).Decision);
        Assert.Equal(Decision.ManualReview, policy.Decide(new ModelPrediction(ContentLabel.Unacceptable, 0.89)).Decision);
        Assert.Equal(Decision.Reject, policy.Decide(new ModelPrediction(ContentLabel.Unacceptable, 0.90)).Decision);
    }

    [Fact]
    public void Decide_GivesNoReasons_ForAutomaticDecisions()
    {
        Assert.Empty(_policy.Decide(new ModelPrediction(ContentLabel.Acceptable, 0.9, signals: Signals())).ReviewReasons);
        Assert.Empty(_policy.Decide(new ModelPrediction(ContentLabel.Unacceptable, 0.95)).ReviewReasons);
    }

    [Theory]
    [InlineData(0.36, 0.0, 0.0, ReviewReason.Toxicity)]
    [InlineData(0.0, 0.51, 0.0, ReviewReason.Spam)]
    [InlineData(0.0, 0.0, 0.51, ReviewReason.PersonalData)]
    public void Decide_SendsAConfidentApprovalToReview_WhenASignalIsAboveItsLimit(
        double toxicity,
        double spam,
        double personalData,
        ReviewReason reason)
    {
        var prediction = new ModelPrediction(ContentLabel.Acceptable, 0.99, signals: Signals(toxicity, spam, personalData));

        var decision = _policy.Decide(prediction);

        Assert.Equal(Decision.ManualReview, decision.Decision);
        Assert.Equal([reason], decision.ReviewReasons);
    }

    [Fact]
    public void Decide_Approves_WhenSignalsAreExactlyAtTheirLimits()
    {
        var decision = _policy.Decide(new ModelPrediction(ContentLabel.Acceptable, 0.85, signals: Signals(0.35, 0.5, 0.5)));

        Assert.Equal(Decision.Approve, decision.Decision);
    }

    [Fact]
    public void Decide_ReportsEveryReason()
    {
        var decision = _policy.Decide(new ModelPrediction(ContentLabel.Acceptable, 0.6, signals: Signals(0.9, 0.9, 0.9)));

        Assert.Equal(Decision.ManualReview, decision.Decision);
        Assert.Equal(
            [ReviewReason.LowConfidence, ReviewReason.Toxicity, ReviewReason.Spam, ReviewReason.PersonalData],
            decision.ReviewReasons);
    }

    [Fact]
    public void Decide_RejectsOnTheLabelAlone_WhateverTheSignals()
    {
        var clean = new ModelPrediction(ContentLabel.Unacceptable, 0.95, signals: Signals());
        var flagged = new ModelPrediction(ContentLabel.Unacceptable, 0.95, signals: Signals(1, 1, 1));

        Assert.Equal(Decision.Reject, _policy.Decide(clean).Decision);
        Assert.Equal(Decision.Reject, _policy.Decide(flagged).Decision);
    }

    [Fact]
    public void Decide_ReportsSignals_ForAnUnconfidentRejection()
    {
        var decision = _policy.Decide(new ModelPrediction(ContentLabel.Unacceptable, 0.7, signals: Signals(spam: 0.8)));

        Assert.Equal(Decision.ManualReview, decision.Decision);
        Assert.Equal([ReviewReason.LowConfidence, ReviewReason.Spam], decision.ReviewReasons);
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

    [Theory]
    [InlineData(-0.01, 0.5, 0.5)]
    [InlineData(0.35, 1.01, 0.5)]
    [InlineData(0.35, 0.5, double.NaN)]
    public void Constructor_Throws_WhenASignalLimitIsOutOfRange(double toxicity, double spam, double personalData)
    {
        var options = new DecisionPolicyOptions
        {
            MaxToxicityForApprove = toxicity,
            MaxSpamForApprove = spam,
            MaxPersonalDataForApprove = personalData,
        };

        Assert.Throws<ArgumentException>(() => new DecisionPolicy(options));
    }

    [Fact]
    public void Constructor_Throws_WhenOptionsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DecisionPolicy(null!));
    }

    private static ModerationSignals Signals(double toxicity = 0, double spam = 0, double personalData = 0) =>
        new(toxicity, spam, personalData);
}
