namespace CommentAnalysis.Core.Messages;

/// <summary>
/// Incoming event: a user created a comment under a post and it awaits moderation.
/// </summary>
public sealed record CommentCreated
{
    public required Guid CommentId { get; init; }

    public required Guid PostId { get; init; }

    public required Guid AuthorId { get; init; }

    public required string Content { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
