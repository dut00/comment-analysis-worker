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
