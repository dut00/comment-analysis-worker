using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace CommentAnalysis.Core.Tests.Analysis;

public class CommentAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly DecisionPolicy _policy = new(new DecisionPolicyOptions());

    [Theory]
    [InlineData(ContentLabel.Acceptable, 0.97, Decision.Approve)]
    [InlineData(ContentLabel.Unacceptable, 0.95, Decision.Reject)]
    [InlineData(ContentLabel.Unacceptable, 0.60, Decision.ManualReview)]
    public async Task AnalyzeAsync_AppliesPolicyToPrediction(ContentLabel label, double confidence, Decision expected)
    {
        var analyzer = CreateAnalyzer(new FakeDecisionModel(new ModelPrediction(label, confidence)));

        var result = await analyzer.AnalyzeAsync("some comment", CancellationToken.None);

        Assert.Equal(expected, result.Decision);
        Assert.Equal(confidence, result.Confidence);
    }

    [Fact]
    public async Task AnalyzeAsync_PassesContentToModel()
    {
        var model = new FakeDecisionModel(new ModelPrediction(ContentLabel.Acceptable, 0.9));
        var analyzer = CreateAnalyzer(model);

        await analyzer.AnalyzeAsync("Great post, thanks!", CancellationToken.None);

        Assert.Equal(["Great post, thanks!"], model.EvaluatedContents);
    }

    [Fact]
    public async Task AnalyzeAsync_RecordsModelInfoAndPrediction()
    {
        var info = new ModelInfo("toxicity", "toxicity@1.0.0+abcdef123456");
        var prediction = new ModelPrediction(
            ContentLabel.Unacceptable,
            0.93,
            new Dictionary<string, double> { ["toxic"] = 0.93, ["non-toxic"] = 0.07 });
        var analyzer = CreateAnalyzer(new FakeDecisionModel(prediction, info));

        var result = await analyzer.AnalyzeAsync("comment", CancellationToken.None);

        Assert.Equal(info, result.Model);
        Assert.Equal(info, analyzer.Model);
        Assert.Same(prediction, result.Prediction);
    }

    [Fact]
    public async Task AnalyzeAsync_MeasuresModelDurationAndTimestamp()
    {
        var model = new FakeDecisionModel(new ModelPrediction(ContentLabel.Acceptable, 0.9))
        {
            OnEvaluate = () => _timeProvider.Advance(TimeSpan.FromMilliseconds(120)),
        };
        var analyzer = CreateAnalyzer(model);

        var result = await analyzer.AnalyzeAsync("comment", CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(120), result.Duration);
        Assert.Equal(Now.AddMilliseconds(120), result.AnalyzedAt);
    }

    [Fact]
    public async Task AnalyzeAsync_PropagatesCancellation()
    {
        var analyzer = CreateAnalyzer(new FakeDecisionModel(new ModelPrediction(ContentLabel.Acceptable, 0.9)));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync("comment", new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task AnalyzeAsync_Throws_WhenContentIsNull()
    {
        var analyzer = CreateAnalyzer(new FakeDecisionModel(new ModelPrediction(ContentLabel.Acceptable, 0.9)));

        await Assert.ThrowsAsync<ArgumentNullException>(() => analyzer.AnalyzeAsync(null!, CancellationToken.None));
    }

    private CommentAnalyzer CreateAnalyzer(IDecisionModel model) => new(model, _policy, _timeProvider);
}
