namespace CommentAnalysis.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>
    /// AMQP URI including credentials and virtual host, e.g. <c>amqps://user:pass@host:5671/vhost</c>. Outside
    /// Development it must use TLS (<c>amqps</c>), unless <see cref="AllowPlaintext"/> is set.
    /// </summary>
    public Uri? Uri { get; set; }

    /// <summary>
    /// Allows <c>amqp://</c> without TLS outside Development. Only for a broker on the same host or private network,
    /// e.g. compose; never in production.
    /// </summary>
    public bool AllowPlaintext { get; set; }

    /// <summary>Shown in the RabbitMQ management UI to identify the connection.</summary>
    public string ClientName { get; set; } = "comment-analysis-worker";

    /// <summary>Topic exchange shared with the comment service; carries both incoming and outgoing events.</summary>
    public string Exchange { get; set; } = "comments";

    public string InputQueue { get; set; } = "comment-analysis.comment-created";

    public string InputRoutingKey { get; set; } = "comment.created";

    public string OutputRoutingKey { get; set; } = "comment.analyzed";

    public string DeadLetterExchange { get; set; } = "comment-analysis.dead-letter";

    public string DeadLetterQueue { get; set; } = "comment-analysis.comment-created.dlq";

    /// <summary>
    /// How many times a message may be returned to the input queue because its consumer failed (channel or
    /// connection closed without an ack, e.g. the worker crashed) before RabbitMQ dead-letters it. Protects
    /// against poison messages that crash the worker. In RabbitMQ 4 an explicit nack/reject with requeue does
    /// not count towards this limit, so retries of handled errors need their own mechanism.
    /// Declared as the queue's <c>x-delivery-limit</c>.
    /// Queue arguments are fixed once the queue exists: changing this value later requires recreating the
    /// queue or overriding it with a RabbitMQ policy, otherwise declaration fails with PRECONDITION_FAILED.
    /// </summary>
    public int DeliveryLimit { get; set; } = 5;

    /// <summary>Upper bound of the exponential back-off between connection attempts.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);
}
