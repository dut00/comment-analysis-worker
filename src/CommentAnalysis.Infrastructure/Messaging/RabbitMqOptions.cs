namespace CommentAnalysis.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>AMQP URI including credentials and virtual host, e.g. <c>amqp://user:pass@host:5672/</c>.</summary>
    public Uri? Uri { get; set; }

    /// <summary>Shown in the RabbitMQ management UI to identify the connection.</summary>
    public string ClientName { get; set; } = "comment-analysis-worker";

    /// <summary>Topic exchange shared with the comment service; carries both incoming and outgoing events.</summary>
    public string Exchange { get; set; } = "comments";

    public string InputQueue { get; set; } = "comment-analysis.comment-created";

    public string InputRoutingKey { get; set; } = "comment.created";

    public string OutputRoutingKey { get; set; } = "comment.analyzed";

    public string DeadLetterExchange { get; set; } = "comment-analysis.dead-letter";

    public string DeadLetterQueue { get; set; } = "comment-analysis.comment-created.dlq";

    /// <summary>Upper bound of the exponential back-off between connection attempts.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);
}
