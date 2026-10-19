namespace CommentAnalysis.Worker.Consumers;

public sealed class ConsumerOptions
{
    public const string SectionName = "Consumer";

    /// <summary>How many messages are processed at the same time by one worker instance.</summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>
    /// How many unacknowledged messages RabbitMQ sends ahead to this worker (basic.qos). Must be at least
    /// <see cref="Concurrency"/>; a little more keeps the worker busy without hoarding messages.
    /// </summary>
    public int Prefetch { get; set; } = 4;

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

        return errors;
    }
}
