using CommentAnalysis.Worker.Outbox;

namespace CommentAnalysis.IntegrationTests.Outbox;

public class OutboxOptionsTests
{
    [Fact]
    public void Defaults_AreValid() => Assert.Empty(new OutboxOptions().GetValidationErrors());

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void BatchSize_OutsideRange_IsInvalid(int batchSize) =>
        Assert.Contains(new OutboxOptions { BatchSize = batchSize }.GetValidationErrors(), error => error.Contains("BatchSize", StringComparison.Ordinal));

    [Fact]
    public void ZeroPollInterval_IsInvalid() =>
        Assert.Contains(new OutboxOptions { PollInterval = TimeSpan.Zero }.GetValidationErrors(), error => error.Contains("PollInterval", StringComparison.Ordinal));

    [Fact]
    public void ZeroPublishTimeout_IsInvalid() =>
        Assert.Contains(new OutboxOptions { PublishTimeout = TimeSpan.Zero }.GetValidationErrors(), error => error.Contains("PublishTimeout", StringComparison.Ordinal));

    [Fact]
    public void NegativeRetention_IsInvalid() =>
        Assert.Contains(new OutboxOptions { Retention = TimeSpan.FromSeconds(-1) }.GetValidationErrors(), error => error.Contains("Retention", StringComparison.Ordinal));

    [Fact]
    public void CleanupIntervalAboveOneDay_IsInvalid() =>
        Assert.Contains(new OutboxOptions { CleanupInterval = TimeSpan.FromDays(2) }.GetValidationErrors(), error => error.Contains("CleanupInterval", StringComparison.Ordinal));
}
