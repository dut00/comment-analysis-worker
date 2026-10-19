using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Persistence;
using CommentAnalysis.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CommentAnalysis.IntegrationTests.Persistence;

[Collection(PostgresTestGroup.Name)]
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
        new ModelPrediction(
            ContentLabel.Unacceptable,
            0.93,
            new Dictionary<string, double> { ["toxic"] = 0.93, ["non-toxic"] = 0.07 }),
        model,
        new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero),
        TimeSpan.FromMilliseconds(42.4));
}
