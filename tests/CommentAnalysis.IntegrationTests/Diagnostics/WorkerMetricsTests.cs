using System.Diagnostics.Metrics;
using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Worker.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;

namespace CommentAnalysis.IntegrationTests.Diagnostics;

public sealed class WorkerMetricsTests : IDisposable
{
    private static readonly ModelInfo Model = new("basal-2.0-mini", "basal-2.0-mini@2.0+cef8109c93be");

    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly WorkerMetrics _metrics;

    public WorkerMetricsTests() => _metrics = new WorkerMetrics(MeterFactory, _time);

    private IMeterFactory MeterFactory => _services.GetRequiredService<IMeterFactory>();

    public void Dispose() => _services.Dispose();

    [Fact]
    public void Analyzed_CountsTheDecisionAndTheReasons_AndRecordsTheModelDuration()
    {
        using var analyzed = Collect<long>("comment_analysis.comments.analyzed");
        using var reasons = Collect<long>("comment_analysis.comments.review_reasons");
        using var duration = Collect<double>("comment_analysis.model.duration");

        _metrics.Analyzed(Analysis(Decision.ManualReview, [ReviewReason.LowConfidence, ReviewReason.Spam], TimeSpan.FromSeconds(11)));

        var decision = Assert.Single(analyzed.GetMeasurementSnapshot());
        Assert.Equal(1, decision.Value);
        Assert.Equal("ManualReview", decision.Tags["comment_analysis.decision"]);
        Assert.Equal(Model.Version, decision.Tags["comment_analysis.model.version"]);
        Assert.Equal(
            ["LowConfidence", "Spam"],
            reasons.GetMeasurementSnapshot().Select(measurement => (string)measurement.Tags["comment_analysis.review_reason"]!));
        Assert.Equal(11, Assert.Single(duration.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public void Analyzed_WithoutAPrediction_RecordsNoModelDuration()
    {
        using var analyzed = Collect<long>("comment_analysis.comments.analyzed");
        using var duration = Collect<double>("comment_analysis.model.duration");

        // A too long comment is not sent to the model.
        _metrics.Analyzed(Analysis(Decision.ManualReview, [ReviewReason.TooLong], TimeSpan.Zero, withPrediction: false));

        Assert.Single(analyzed.GetMeasurementSnapshot());
        Assert.Empty(duration.GetMeasurementSnapshot());
    }

    [Theory]
    [InlineData(nameof(DeadLetterReason.TooLarge), "too_large")]
    [InlineData(nameof(DeadLetterReason.Malformed), "malformed")]
    [InlineData(nameof(DeadLetterReason.RetriesExhausted), "retries_exhausted")]
    public void DeadLettered_IsTaggedWithTheReason(string reason, string tag)
    {
        using var deadLettered = Collect<long>("comment_analysis.messages.dead_lettered");

        _metrics.DeadLettered(Enum.Parse<DeadLetterReason>(reason));

        Assert.Equal(tag, Assert.Single(deadLettered.GetMeasurementSnapshot()).Tags["comment_analysis.dead_letter_reason"]);
    }

    [Fact]
    public void OutboxLag_IsUnknownUntilChecked_ThenTheAgeOfTheOldestPendingMessage_OrZero()
    {
        using var lag = Collect<double>("comment_analysis.outbox.oldest_pending_age");

        lag.RecordObservableInstruments();
        Assert.Empty(lag.GetMeasurementSnapshot());

        _metrics.OutboxChecked(_time.GetUtcNow().AddSeconds(-90));
        _time.Advance(TimeSpan.FromSeconds(10));
        lag.RecordObservableInstruments();
        Assert.Equal(100, lag.LastMeasurement!.Value);

        _metrics.OutboxChecked(null);
        lag.RecordObservableInstruments();
        Assert.Equal(0, lag.LastMeasurement!.Value);

        _metrics.OutboxCheckFailed();
        lag.Clear();
        lag.RecordObservableInstruments();
        Assert.Empty(lag.GetMeasurementSnapshot());
    }

    private MetricCollector<T> Collect<T>(string instrument)
        where T : struct => new(MeterFactory, WorkerMetrics.MeterName, instrument, _time);

    private static AnalysisResult Analysis(Decision decision, IReadOnlyList<ReviewReason> reasons, TimeSpan duration, bool withPrediction = true) => new(
        decision,
        reasons,
        withPrediction ? new ModelPrediction(ContentLabel.Acceptable, 0.7) : null,
        Model,
        DateTimeOffset.UtcNow,
        duration);
}
