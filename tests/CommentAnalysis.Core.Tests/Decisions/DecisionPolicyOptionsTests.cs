using CommentAnalysis.Core.Decisions;

namespace CommentAnalysis.Core.Tests.Decisions;

public class DecisionPolicyOptionsTests
{
    [Fact]
    public void GetValidationErrors_ReturnsEmpty_ForDefaults()
    {
        Assert.Empty(new DecisionPolicyOptions().GetValidationErrors());
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    public void GetValidationErrors_ReturnsEmpty_ForBoundaryValues(double approveThreshold, double rejectThreshold)
    {
        var options = new DecisionPolicyOptions
        {
            ApproveThreshold = approveThreshold,
            RejectThreshold = rejectThreshold,
        };

        Assert.Empty(options.GetValidationErrors());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void GetValidationErrors_ReturnsEmpty_ForBoundarySignalLimits(double limit)
    {
        var options = new DecisionPolicyOptions
        {
            MaxToxicityForApprove = limit,
            MaxSpamForApprove = limit,
            MaxPersonalDataForApprove = limit,
        };

        Assert.Empty(options.GetValidationErrors());
    }

    [Fact]
    public void GetValidationErrors_ReportsEveryInvalidSignalLimit()
    {
        var options = new DecisionPolicyOptions
        {
            MaxToxicityForApprove = -0.1,
            MaxSpamForApprove = 1.1,
            MaxPersonalDataForApprove = double.PositiveInfinity,
        };

        var errors = options.GetValidationErrors();

        Assert.Collection(
            errors,
            error => Assert.Contains("DecisionPolicy:MaxToxicityForApprove", error, StringComparison.Ordinal),
            error => Assert.Contains("DecisionPolicy:MaxSpamForApprove", error, StringComparison.Ordinal),
            error => Assert.Contains("DecisionPolicy:MaxPersonalDataForApprove", error, StringComparison.Ordinal));
    }

    [Fact]
    public void GetValidationErrors_ReportsEveryInvalidThreshold()
    {
        var options = new DecisionPolicyOptions
        {
            ApproveThreshold = 0.1,
            RejectThreshold = 2.0,
        };

        var errors = options.GetValidationErrors();

        Assert.Collection(
            errors,
            error => Assert.Contains("DecisionPolicy:ApproveThreshold", error, StringComparison.Ordinal),
            error => Assert.Contains("DecisionPolicy:RejectThreshold", error, StringComparison.Ordinal));
    }
}
