using System.Diagnostics.Metrics;
using CommentAnalysis.Core.Analysis;

namespace CommentAnalysis.Worker.Diagnostics;

/// <summary>
/// The worker's metrics (meter <see cref="MeterName"/>). Tags have a small, fixed set of values: decisions, review
/// reasons, dead-letter and requeue reasons and the model version, never comment data.
/// </summary>
internal sealed class WorkerMetrics
{
    public const string MeterName = "CommentAnalysis.Worker";

    /// <summary>Stored in <see cref="_oldestPendingTicks"/> when nothing waits.</summary>
    private const long NoPendingOutboxMessage = 0;

    /// <summary>Stored in <see cref="_oldestPendingTicks"/> until the outbox was checked, and while it cannot be.</summary>
    private const long OutboxUnknown = -1;

    /// <summary>Seconds; the local model takes 10–120 s, a cloud model well under a second.</summary>
    private static readonly double[] ModelDurationBuckets = [0.1, 0.25, 0.5, 1, 2.5, 5, 10, 20, 30, 60, 120, 300];

    private readonly Counter<long> _analyzed;
    private readonly Counter<long> _reviewReasons;
    private readonly Counter<long> _duplicates;
    private readonly Histogram<double> _modelDuration;
    private readonly Counter<long> _requeued;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _outboxPublished;

    // The creation time of the oldest unpublished outbox message, or one of the constants below.
    private long _oldestPendingTicks = OutboxUnknown;

    public WorkerMetrics(IMeterFactory meterFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var meter = meterFactory.Create(MeterName);

        _analyzed = meter.CreateCounter<long>(
            "comment_analysis.comments.analyzed", "{comment}", "Comments analyzed and stored, by decision and model version.");
        _reviewReasons = meter.CreateCounter<long>(
            "comment_analysis.comments.review_reasons", "{reason}", "Reasons for sending comments to manual review, by model version.");
        _duplicates = meter.CreateCounter<long>(
            "comment_analysis.comments.duplicates", "{comment}", "Comments acknowledged without processing, because the model version already analyzed them.");
        _modelDuration = meter.CreateHistogram(
            "comment_analysis.model.duration",
            "s",
            "How long the model took to evaluate a comment (successful evaluations only).",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = ModelDurationBuckets });
        _requeued = meter.CreateCounter<long>(
            "comment_analysis.messages.requeued", "{message}", "Messages returned to the queue without counting a retry, by reason.");
        _retries = meter.CreateCounter<long>(
            "comment_analysis.messages.retried", "{message}", "Counted retries scheduled after a processing failure.");
        _deadLettered = meter.CreateCounter<long>(
            "comment_analysis.messages.dead_lettered", "{message}", "Messages rejected to the dead-letter queue, by reason.");
        _outboxPublished = meter.CreateCounter<long>(
            "comment_analysis.outbox.published", "{message}", "Outbox messages published and confirmed by the broker.");
        meter.CreateObservableGauge(
            "comment_analysis.outbox.oldest_pending_age",
            () => ObserveOldestPendingAge(timeProvider),
            "s",
            "Age of the oldest unpublished outbox message, 0 when none waits (every replica reports the same value).");
    }

    public void Analyzed(AnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var version = new KeyValuePair<string, object?>("comment_analysis.model.version", analysis.Model.Version);
        _analyzed.Add(1, new KeyValuePair<string, object?>("comment_analysis.decision", analysis.Decision.ToString()), version);
        foreach (var reason in analysis.ReviewReasons)
        {
            _reviewReasons.Add(1, new KeyValuePair<string, object?>("comment_analysis.review_reason", reason.ToString()), version);
        }

        if (analysis.Prediction is not null)
        {
            _modelDuration.Record(analysis.Duration.TotalSeconds, version);
        }
    }

    public void Duplicate() => _duplicates.Add(1);

    public void Requeued(RequeueReason reason) =>
        _requeued.Add(1, new KeyValuePair<string, object?>("comment_analysis.requeue_reason", reason switch
        {
            RequeueReason.ModelUnavailable => "model_unavailable",
            RequeueReason.RetryPublishFailed => "retry_publish_failed",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        }));

    public void Retried() => _retries.Add(1);

    public void DeadLettered(DeadLetterReason reason) =>
        _deadLettered.Add(1, new KeyValuePair<string, object?>("comment_analysis.dead_letter_reason", reason switch
        {
            DeadLetterReason.TooLarge => "too_large",
            DeadLetterReason.Malformed => "malformed",
            DeadLetterReason.RetriesExhausted => "retries_exhausted",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        }));

    public void OutboxPublished(int count) => _outboxPublished.Add(count);

    /// <summary>Records the creation time of the oldest unpublished outbox message, or null if none waits.</summary>
    public void OutboxChecked(DateTimeOffset? oldestPending) =>
        Interlocked.Exchange(ref _oldestPendingTicks, oldestPending?.UtcTicks ?? NoPendingOutboxMessage);

    /// <summary>The outbox could not be checked (e.g. the database is down): no value is reported until it can.</summary>
    public void OutboxCheckFailed() => Interlocked.Exchange(ref _oldestPendingTicks, OutboxUnknown);

    private IEnumerable<Measurement<double>> ObserveOldestPendingAge(TimeProvider timeProvider)
    {
        var ticks = Interlocked.Read(ref _oldestPendingTicks);
        if (ticks == OutboxUnknown)
        {
            yield break;
        }

        yield return new Measurement<double>(ticks == NoPendingOutboxMessage
            ? 0
            : Math.Max(0, (timeProvider.GetUtcNow().UtcTicks - ticks) / (double)TimeSpan.TicksPerSecond));
    }
}

internal enum RequeueReason
{
    ModelUnavailable,
    RetryPublishFailed,
}

internal enum DeadLetterReason
{
    TooLarge,
    Malformed,
    RetriesExhausted,
}
