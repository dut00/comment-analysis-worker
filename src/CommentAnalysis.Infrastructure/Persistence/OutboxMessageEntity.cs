using System.Text;
using CommentAnalysis.Core.Messages;

namespace CommentAnalysis.Infrastructure.Persistence;

/// <summary>
/// An outgoing message waiting to be published (transactional outbox). It is written in the same transaction as
/// the data it describes, so the message exists if and only if that data was committed.
/// </summary>
public sealed class OutboxMessageEntity
{
    public const int TypeMaxLength = 100;
    public const string UnpublishedIndex = "ix_outbox_messages_unpublished";

    /// <summary>Time-ordered (UUIDv7); also used as the AMQP message id, so consumers can deduplicate.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The stable message type, e.g. <see cref="CommentAnalyzed.MessageType"/>; the publisher maps it to a routing key.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>The message serialized with <see cref="MessageSerializer"/>.</summary>
    public required string Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the broker confirmed the message; <c>null</c> while it is waiting to be published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public static OutboxMessageEntity Create<T>(string type, T message, DateTimeOffset createdAt)
        where T : notnull =>
        new()
        {
            Id = Guid.CreateVersion7(createdAt),
            Type = type,
            Payload = Encoding.UTF8.GetString(MessageSerializer.Serialize(message)),
            CreatedAt = createdAt,
        };
}
