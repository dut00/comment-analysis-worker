namespace CommentAnalysis.Worker.Consumers;

public sealed class ConsumerOptions
{
    public const string SectionName = "Consumer";

    private const int MinMessageBytes = 1024;
    private const int MaxMessageBytesLimit = 16 * 1024 * 1024;
    private const int MaxRetriesLimit = 100;

    /// <summary>
    /// Upper bound of <see cref="RetryDelay"/> and of any single retry's wait. A retry holds a processing slot and
    /// an unacked delivery while it waits, so the wait must stay far below RabbitMQ's <c>consumer_timeout</c>
    /// (30 min by default); longer delays belong in a delayed-retry queue instead.
    /// </summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>How many messages are processed at the same time by one worker instance.</summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>
    /// How many unacknowledged messages RabbitMQ sends ahead to this worker (basic.qos). Must be at least
    /// <see cref="Concurrency"/>; a little more keeps the worker busy without hoarding messages.
    /// </summary>
    public int Prefetch { get; set; } = 4;

    /// <summary>
    /// Largest accepted message body in bytes; bigger messages are dead-lettered without being deserialized.
    /// </summary>
    public int MaxMessageBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Longest accepted comment content in characters; longer comments are dead-lettered instead of being sent
    /// to the model, whose cost grows with the input size.
    /// </summary>
    public int MaxContentLength { get; set; } = 10_000;

    /// <summary>
    /// How many times a message whose processing failed is retried before it is dead-lettered. Retries are
    /// republished copies carrying an <c>x-retry-count</c> header (a requeue would not be counted by RabbitMQ).
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Delay before the first retry; each further retry waits one more multiple of it, up to 30 s.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (Concurrency is < 1 or > ushort.MaxValue)
        {
            errors.Add($"{SectionName}:Concurrency must be within [1, {ushort.MaxValue}], but was {Concurrency}.");
        }

        if (Prefetch < Concurrency || Prefetch > ushort.MaxValue)
        {
            errors.Add($"{SectionName}:Prefetch must be within [Concurrency, {ushort.MaxValue}], but was {Prefetch}.");
        }

        if (MaxMessageBytes is < MinMessageBytes or > MaxMessageBytesLimit)
        {
            errors.Add($"{SectionName}:MaxMessageBytes must be within [{MinMessageBytes}, {MaxMessageBytesLimit}], but was {MaxMessageBytes}.");
        }

        if (MaxContentLength < 1 || MaxContentLength > MaxMessageBytes)
        {
            errors.Add($"{SectionName}:MaxContentLength must be within [1, MaxMessageBytes], but was {MaxContentLength}.");
        }

        if (MaxRetries is < 0 or > MaxRetriesLimit)
        {
            errors.Add($"{SectionName}:MaxRetries must be within [0, {MaxRetriesLimit}], but was {MaxRetries}.");
        }

        if (RetryDelay < TimeSpan.Zero || RetryDelay > MaxRetryDelay)
        {
            errors.Add($"{SectionName}:RetryDelay must be within [00:00:00, {MaxRetryDelay}], but was {RetryDelay}.");
        }

        return errors;
    }
}
