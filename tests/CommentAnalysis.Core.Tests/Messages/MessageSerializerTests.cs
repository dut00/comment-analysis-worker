using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Messages;

namespace CommentAnalysis.Core.Tests.Messages;

public class MessageSerializerTests
{
    private static readonly CommentCreated SampleComment = new()
    {
        CommentId = Guid.Parse("6f1c2a3e-0b7d-4c55-9a8e-1d2f3a4b5c6d"),
        PostId = Guid.Parse("a1b2c3d4-e5f6-4789-8abc-def012345678"),
        AuthorId = Guid.Parse("0f9e8d7c-6b5a-4f3e-8d2c-1b0a9f8e7d6c"),
        Content = "Great post, thanks!",
        CreatedAt = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void CommentCreated_RoundTrips()
    {
        var bytes = MessageSerializer.Serialize(SampleComment);

        Assert.Equal(SampleComment, MessageSerializer.Deserialize<CommentCreated>(bytes));
    }

    [Fact]
    public void CommentCreated_UsesCamelCasePropertyNames()
    {
        var json = ToJsonObject(MessageSerializer.Serialize(SampleComment));

        Assert.Equal(
            ["commentId", "postId", "authorId", "content", "createdAt"],
            json.Select(property => property.Key));
    }

    [Fact]
    public void CommentAnalyzed_SerializesDecisionAsCamelCaseString()
    {
        var message = CreateAnalyzed(Decision.ManualReview);

        var json = ToJsonObject(MessageSerializer.Serialize(message));

        Assert.Equal("manualReview", json["decision"]!.GetValue<string>());
        Assert.Equal(0.72, json["confidence"]!.GetValue<double>());
        Assert.Equal("toxicity@1.0.0+abcdef123456", json["modelVersion"]!.GetValue<string>());
    }

    [Fact]
    public void CommentAnalyzed_RoundTrips()
    {
        var message = CreateAnalyzed(Decision.Reject);

        var bytes = MessageSerializer.Serialize(message);

        Assert.Equal(message, MessageSerializer.Deserialize<CommentAnalyzed>(bytes));
    }

    [Fact]
    public void Deserialize_IgnoresUnknownProperties()
    {
        const string json = """
            {
              "commentId": "6f1c2a3e-0b7d-4c55-9a8e-1d2f3a4b5c6d",
              "postId": "a1b2c3d4-e5f6-4789-8abc-def012345678",
              "authorId": "0f9e8d7c-6b5a-4f3e-8d2c-1b0a9f8e7d6c",
              "content": "Great post, thanks!",
              "createdAt": "2026-10-19T12:00:00+00:00",
              "language": "en"
            }
            """;

        Assert.Equal(SampleComment, MessageSerializer.Deserialize<CommentCreated>(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("""{ "postId": "a1b2c3d4-e5f6-4789-8abc-def012345678", "authorId": "0f9e8d7c-6b5a-4f3e-8d2c-1b0a9f8e7d6c", "content": "x", "createdAt": "2026-10-19T12:00:00Z" }""")]
    [InlineData("""{ "commentId": "6f1c2a3e-0b7d-4c55-9a8e-1d2f3a4b5c6d", "postId": "a1b2c3d4-e5f6-4789-8abc-def012345678", "authorId": "0f9e8d7c-6b5a-4f3e-8d2c-1b0a9f8e7d6c", "createdAt": "2026-10-19T12:00:00Z" }""")]
    [InlineData("""{ "commentId": "6f1c2a3e-0b7d-4c55-9a8e-1d2f3a4b5c6d", "postId": "a1b2c3d4-e5f6-4789-8abc-def012345678", "authorId": "0f9e8d7c-6b5a-4f3e-8d2c-1b0a9f8e7d6c", "content": null, "createdAt": "2026-10-19T12:00:00Z" }""")]
    [InlineData("""{ "commentId": "not-a-guid", "postId": "a1b2c3d4-e5f6-4789-8abc-def012345678", "authorId": "0f9e8d7c-6b5a-4f3e-8d2c-1b0a9f8e7d6c", "content": "x", "createdAt": "2026-10-19T12:00:00Z" }""")]
    [InlineData("null")]
    [InlineData("not json")]
    public void Deserialize_Throws_WhenPayloadIsMalformed(string json)
    {
        Assert.ThrowsAny<JsonException>(() => MessageSerializer.Deserialize<CommentCreated>(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Deserialize_Throws_WhenDecisionIsInteger()
    {
        var json = ToJsonObject(MessageSerializer.Serialize(CreateAnalyzed(Decision.Approve)));
        json["decision"] = 0;

        Assert.Throws<JsonException>(
            () => MessageSerializer.Deserialize<CommentAnalyzed>(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    private static CommentAnalyzed CreateAnalyzed(Decision decision) => new()
    {
        CommentId = SampleComment.CommentId,
        PostId = SampleComment.PostId,
        Decision = decision,
        Confidence = 0.72,
        ModelName = "toxicity",
        ModelVersion = "toxicity@1.0.0+abcdef123456",
        AnalyzedAt = new DateTimeOffset(2026, 10, 19, 12, 0, 1, TimeSpan.Zero),
    };

    private static JsonObject ToJsonObject(byte[] utf8Json) => JsonNode.Parse(utf8Json)!.AsObject();
}
