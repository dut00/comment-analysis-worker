using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Messages;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Core.Tests.Fakes;

namespace CommentAnalysis.Core.Tests.Analysis;

public class CommentProcessorTests
{
    private const int MaxContentLength = 20;

    private static readonly ModelInfo Model = new("toxicity", "toxicity@1.0.0+abcdef123456");

    private readonly FakeDecisionModel _model = new(new ModelPrediction(ContentLabel.Unacceptable, 0.95), Model);
    private readonly InMemoryAnalysisStore _store = new();
    private readonly CommentProcessor _processor;

    public CommentProcessorTests()
    {
        var analyzer = new CommentAnalyzer(_model, new DecisionPolicy(new DecisionPolicyOptions()), TimeProvider.System);
        _processor = new CommentProcessor(analyzer, _store);
    }

    [Fact]
    public async Task ProcessAsync_AnalyzesAndStoresNewComment()
    {
        var comment = CreateComment("You are an idiot");

        var result = await _processor.ProcessAsync(comment, MaxContentLength, CancellationToken.None);

        Assert.Equal(ProcessingStatus.Analyzed, result.Status);
        Assert.Equal(Decision.Reject, result.Analysis!.Decision);
        Assert.Equal(["You are an idiot"], _model.EvaluatedContents);
        Assert.True(_store.Rows.ContainsKey((comment.CommentId, Model.Version)));
    }

    [Fact]
    public async Task ProcessAsync_SkipsModel_WhenCommentAlreadyAnalyzedByCurrentModelVersion()
    {
        var comment = CreateComment("Nice post");
        await _processor.ProcessAsync(comment, MaxContentLength, CancellationToken.None);

        var result = await _processor.ProcessAsync(comment, MaxContentLength, CancellationToken.None);

        Assert.Equal(ProcessingStatus.AlreadyAnalyzed, result.Status);
        Assert.Null(result.Analysis);
        Assert.Equal(Model, result.Model);
        Assert.Single(_model.EvaluatedContents);
        Assert.Single(_store.Rows);
    }

    [Fact]
    public async Task ProcessAsync_ReportsAlreadyAnalyzed_WhenAnotherWorkerSavedFirst()
    {
        _store.LoseNextSaveRace = true;

        var result = await _processor.ProcessAsync(CreateComment("Nice post"), MaxContentLength, CancellationToken.None);

        Assert.Equal(ProcessingStatus.AlreadyAnalyzed, result.Status);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task ProcessAsync_SendsATooLongCommentToManualReview_WithoutTheModel()
    {
        var comment = CreateComment(new string('a', MaxContentLength + 1));

        var result = await _processor.ProcessAsync(comment, MaxContentLength, CancellationToken.None);

        Assert.Equal(ProcessingStatus.Analyzed, result.Status);
        Assert.Equal(Decision.ManualReview, result.Analysis!.Decision);
        Assert.Equal([ReviewReason.TooLong], result.Analysis.ReviewReasons);
        Assert.Null(result.Analysis.Prediction);
        Assert.Null(result.Analysis.Confidence);
        Assert.Equal(Model, result.Analysis.Model);
        Assert.Empty(_model.EvaluatedContents);
        Assert.True(_store.Rows.ContainsKey((comment.CommentId, Model.Version)));
    }

    [Fact]
    public async Task ProcessAsync_EvaluatesACommentExactlyAtTheLimit()
    {
        var result = await _processor.ProcessAsync(CreateComment(new string('a', MaxContentLength)), MaxContentLength, CancellationToken.None);

        Assert.NotNull(result.Analysis!.Prediction);
        Assert.Single(_model.EvaluatedContents);
    }

    [Fact]
    public async Task ProcessAsync_Throws_WhenCommentIsNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _processor.ProcessAsync(null!, MaxContentLength, CancellationToken.None));
    }

    private static CommentCreated CreateComment(string content) => new()
    {
        CommentId = Guid.NewGuid(),
        PostId = Guid.NewGuid(),
        AuthorId = Guid.NewGuid(),
        Content = content,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
