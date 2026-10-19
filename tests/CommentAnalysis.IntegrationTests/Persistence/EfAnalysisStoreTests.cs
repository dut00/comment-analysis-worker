using System.Text;
using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Messages;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Persistence;
using CommentAnalysis.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CommentAnalysis.IntegrationTests.Persistence;

[Collection(ContainersTestGroup.Name)]
public class EfAnalysisStoreTests(PostgresFixture postgres)
{
    private static readonly ModelInfo Model = new("toxicity", "toxicity@1.0.0+abcdef123456");

    [Fact]
    public async Task SaveAsync_PersistsAllFields()
    {
        var commentId = Guid.NewGuid();
        var postId = Guid.NewGuid();
        var result = CreateResult(Model);

        await using (var context = postgres.CreateContext())
        {
            var outcome = await new EfAnalysisStore(context).SaveAsync(commentId, postId, result, CancellationToken.None);
            Assert.Equal(SaveOutcome.Saved, outcome);
        }

        await using var verifyContext = postgres.CreateContext();
        var row = await verifyContext.CommentAnalyses.SingleAsync(a => a.CommentId == commentId);

        Assert.Equal(postId, row.PostId);
        Assert.Equal(Decision.Reject, row.Decision);
        Assert.Equal(ContentLabel.Unacceptable, row.Label);
        Assert.Equal(0.93, row.Confidence);
        Assert.Equal(0.93, row.Probabilities["toxic"]);
        Assert.Equal(0.07, row.Probabilities["non-toxic"]);
        Assert.Equal(Model.Name, row.ModelName);
        Assert.Equal(Model.Version, row.ModelVersion);
        Assert.Equal(result.AnalyzedAt, row.AnalyzedAt);
        Assert.Equal(42, row.DurationMs);
        Assert.Empty(row.ReviewReasons);
        Assert.Null(row.Toxicity);
        Assert.Null(row.SpamProbability);
        Assert.Null(row.PersonalDataProbability);
    }

    [Fact]
    public async Task SaveAsync_PersistsReviewReasonsAndSignals()
    {
        var commentId = Guid.NewGuid();
        var result = CreateResult(Model) with
        {
            Decision = Decision.ManualReview,
            ReviewReasons = [ReviewReason.Spam, ReviewReason.PersonalData],
            Prediction = new ModelPrediction(ContentLabel.Acceptable, 0.96, signals: new ModerationSignals(0.25, 0.94, 0.95)),
        };

        await using (var context = postgres.CreateContext())
        {
            await new EfAnalysisStore(context).SaveAsync(commentId, Guid.NewGuid(), result, CancellationToken.None);
        }

        await using var verifyContext = postgres.CreateContext();
        var row = await verifyContext.CommentAnalyses.SingleAsync(a => a.CommentId == commentId);

        Assert.Equal(Decision.ManualReview, row.Decision);
        Assert.Equal(["Spam", "PersonalData"], row.ReviewReasons);
        Assert.Equal(0.25, row.Toxicity);
        Assert.Equal(0.94, row.SpamProbability);
        Assert.Equal(0.95, row.PersonalDataProbability);
    }

    [Fact]
    public async Task SaveAsync_PersistsACommentTheModelDidNotEvaluate()
    {
        var commentId = Guid.NewGuid();
        var result = CreateResult(Model) with
        {
            Decision = Decision.ManualReview,
            ReviewReasons = [ReviewReason.TooLong],
            Prediction = null,
            Duration = TimeSpan.Zero,
        };

        await using (var context = postgres.CreateContext())
        {
            await new EfAnalysisStore(context).SaveAsync(commentId, Guid.NewGuid(), result, CancellationToken.None);
        }

        await using var verifyContext = postgres.CreateContext();
        var row = await verifyContext.CommentAnalyses.SingleAsync(a => a.CommentId == commentId);

        Assert.Equal(["TooLong"], row.ReviewReasons);
        Assert.Null(row.Label);
        Assert.Null(row.Confidence);
        Assert.Empty(row.Probabilities);
        Assert.Null(row.Toxicity);
        var message = Assert.Single(await GetOutboxMessagesAsync(commentId));
        Assert.Null(MessageSerializer.Deserialize<CommentAnalyzed>(Encoding.UTF8.GetBytes(message.Payload)).Confidence);
    }

    [Theory]
    [InlineData(Decision.ManualReview, false)]
    [InlineData(Decision.Reject, true)]
    public async Task SaveAsync_Fails_WhenTheReviewReasonsDoNotMatchTheDecision(Decision decision, bool withReason)
    {
        var result = CreateResult(Model) with
        {
            Decision = decision,
            ReviewReasons = withReason ? [ReviewReason.LowConfidence] : [],
        };

        await using var context = postgres.CreateContext();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => new EfAnalysisStore(context).SaveAsync(Guid.NewGuid(), Guid.NewGuid(), result, CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_StoresCommentAnalyzedInTheOutbox()
    {
        var commentId = Guid.NewGuid();
        var postId = Guid.NewGuid();
        var result = CreateResult(Model);

        await using (var context = postgres.CreateContext())
        {
            await new EfAnalysisStore(context).SaveAsync(commentId, postId, result, CancellationToken.None);
        }

        var message = Assert.Single(await GetOutboxMessagesAsync(commentId));
        Assert.Equal(CommentAnalyzed.MessageType, message.Type);
        Assert.Null(message.PublishedAt);
        Assert.Equal(result.AnalyzedAt, message.CreatedAt);
        Assert.Equal(
            CommentAnalyzed.From(commentId, postId, result),
            MessageSerializer.Deserialize<CommentAnalyzed>(Encoding.UTF8.GetBytes(message.Payload)));
    }

    [Fact]
    public async Task SaveAsync_StoresNeitherRow_WhenTheTransactionFails()
    {
        var commentId = Guid.NewGuid();

        await using (var context = postgres.CreateContext(new FailAfterOutboxInsertInterceptor()))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                new EfAnalysisStore(context).SaveAsync(commentId, Guid.NewGuid(), CreateResult(Model), CancellationToken.None));
        }

        Assert.Equal(0, await CountRowsAsync(commentId));
        Assert.Empty(await GetOutboxMessagesAsync(commentId));
    }

    [Fact]
    public async Task ExistsAsync_IsTrueOnlyForSameCommentAndModelVersion()
    {
        var commentId = Guid.NewGuid();

        await using (var context = postgres.CreateContext())
        {
            await new EfAnalysisStore(context).SaveAsync(commentId, Guid.NewGuid(), CreateResult(Model), CancellationToken.None);
        }

        await using var queryContext = postgres.CreateContext();
        var store = new EfAnalysisStore(queryContext);

        Assert.True(await store.ExistsAsync(commentId, Model.Version, CancellationToken.None));
        Assert.False(await store.ExistsAsync(commentId, "toxicity@2.0.0+000000000000", CancellationToken.None));
        Assert.False(await store.ExistsAsync(Guid.NewGuid(), Model.Version, CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_ReturnsAlreadyExists_WhenSameCommentAndModelVersionSavedTwice()
    {
        var commentId = Guid.NewGuid();
        var postId = Guid.NewGuid();

        await using (var context = postgres.CreateContext())
        {
            await new EfAnalysisStore(context).SaveAsync(commentId, postId, CreateResult(Model), CancellationToken.None);
        }

        await using (var context = postgres.CreateContext())
        {
            var outcome = await new EfAnalysisStore(context).SaveAsync(commentId, postId, CreateResult(Model), CancellationToken.None);
            Assert.Equal(SaveOutcome.AlreadyExists, outcome);
        }

        Assert.Equal(1, await CountRowsAsync(commentId));
        Assert.Single(await GetOutboxMessagesAsync(commentId));
    }

    private async Task<List<OutboxMessageEntity>> GetOutboxMessagesAsync(Guid commentId)
    {
        await using var context = postgres.CreateContext();
        return await context.OutboxMessages
            .FromSql($"SELECT * FROM outbox_messages WHERE payload ->> 'commentId' = {commentId.ToString()}")
            .AsNoTracking()
            .ToListAsync();
    }

    [Fact]
    public async Task SaveAsync_AllowsSameCommentForDifferentModelVersion()
    {
        var commentId = Guid.NewGuid();
        var postId = Guid.NewGuid();

        await using (var context = postgres.CreateContext())
        {
            await new EfAnalysisStore(context).SaveAsync(commentId, postId, CreateResult(Model), CancellationToken.None);
        }

        await using (var context = postgres.CreateContext())
        {
            var newerModel = new ModelInfo("toxicity", "toxicity@2.0.0+000000000000");
            var outcome = await new EfAnalysisStore(context).SaveAsync(commentId, postId, CreateResult(newerModel), CancellationToken.None);
            Assert.Equal(SaveOutcome.Saved, outcome);
        }

        Assert.Equal(2, await CountRowsAsync(commentId));
    }

    [Fact]
    public async Task SaveAsync_StoresExactlyOneRow_WhenWorkersRaceOnSameComment()
    {
        const int workers = 8;
        var commentId = Guid.NewGuid();
        var postId = Guid.NewGuid();
        var contexts = Enumerable.Range(0, workers).Select(_ => postgres.CreateContext()).ToList();

        try
        {
            // Every worker passed the ExistsAsync check before any of them saved.
            var outcomes = await Task.WhenAll(contexts.Select(context =>
                new EfAnalysisStore(context).SaveAsync(commentId, postId, CreateResult(Model), CancellationToken.None)));

            Assert.Single(outcomes, outcome => outcome == SaveOutcome.Saved);
            Assert.Equal(workers - 1, outcomes.Count(outcome => outcome == SaveOutcome.AlreadyExists));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }

        Assert.Equal(1, await CountRowsAsync(commentId));
    }

    private async Task<int> CountRowsAsync(Guid commentId)
    {
        await using var context = postgres.CreateContext();
        return await context.CommentAnalyses.CountAsync(a => a.CommentId == commentId);
    }

    private static AnalysisResult CreateResult(ModelInfo model) => new(
        Decision.Reject,
        [],
        new ModelPrediction(
            ContentLabel.Unacceptable,
            0.93,
            new Dictionary<string, double> { ["toxic"] = 0.93, ["non-toxic"] = 0.07 }),
        model,
        new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero),
        TimeSpan.FromMilliseconds(42.4));
}
