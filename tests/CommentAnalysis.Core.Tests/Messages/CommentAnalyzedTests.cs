using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Messages;
using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Tests.Messages;

public class CommentAnalyzedTests
{
    [Fact]
    public void From_MapsCommentAndAnalysisResult()
    {
        var comment = new CommentCreated
        {
            CommentId = Guid.NewGuid(),
            PostId = Guid.NewGuid(),
            AuthorId = Guid.NewGuid(),
            Content = "You are an idiot",
            CreatedAt = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero),
        };
        var result = new AnalysisResult(
            Decision.Reject,
            new ModelPrediction(ContentLabel.Unacceptable, 0.96),
            new ModelInfo("toxicity", "toxicity@1.0.0+abcdef123456"),
            new DateTimeOffset(2026, 10, 19, 12, 0, 1, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(40));

        var message = CommentAnalyzed.From(comment, result);

        Assert.Equal(comment.CommentId, message.CommentId);
        Assert.Equal(comment.PostId, message.PostId);
        Assert.Equal(Decision.Reject, message.Decision);
        Assert.Equal(0.96, message.Confidence);
        Assert.Equal("toxicity", message.ModelName);
        Assert.Equal("toxicity@1.0.0+abcdef123456", message.ModelVersion);
        Assert.Equal(result.AnalyzedAt, message.AnalyzedAt);
    }
}
