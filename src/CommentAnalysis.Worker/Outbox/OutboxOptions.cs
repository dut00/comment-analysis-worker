namespace CommentAnalysis.Worker.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    private const int MaxBatchSize = 1000;
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinPublishTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxPublishTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinCleanupInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many messages one replica claims and publishes per database transaction. The rows stay locked
    /// (<c>FOR UPDATE SKIP LOCKED</c>) until the batch is confirmed, so other replicas take the next ones.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>How long to wait before looking for new messages after a batch that was not full.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Limit for publishing one batch and receiving its confirms (e.g. while RabbitMQ blocks publishers).
    /// Messages not confirmed in time stay unpublished and are retried.
    /// </summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long published messages are kept (for diagnostics) before they are deleted.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often published messages older than <see cref="Retention"/> are deleted.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (BatchSize is < 1 or > MaxBatchSize)
        {
            errors.Add($"{SectionName}:BatchSize must be within [1, {MaxBatchSize}], but was {BatchSize}.");
        }

        AddErrorIfOutside(errors, nameof(PollInterval), PollInterval, MinPollInterval, MaxPollInterval);
        AddErrorIfOutside(errors, nameof(PublishTimeout), PublishTimeout, MinPublishTimeout, MaxPublishTimeout);
        AddErrorIfOutside(errors, nameof(Retention), Retention, TimeSpan.Zero, TimeSpan.MaxValue);
        AddErrorIfOutside(errors, nameof(CleanupInterval), CleanupInterval, MinCleanupInterval, TimeSpan.FromDays(1));

        return errors;
    }

    private static void AddErrorIfOutside(List<string> errors, string name, TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{SectionName}:{name} must be within [{min}, {max}], but was {value}.");
        }
    }
}
