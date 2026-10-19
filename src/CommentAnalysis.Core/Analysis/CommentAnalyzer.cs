using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Analysis;

/// <summary>
/// Evaluates a comment with the decision model and applies the decision policy to the prediction.
/// </summary>
public sealed class CommentAnalyzer
{
    private readonly IDecisionModel _model;
    private readonly DecisionPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public CommentAnalyzer(IDecisionModel model, DecisionPolicy policy, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _model = model;
        _policy = policy;
        _timeProvider = timeProvider;
    }

    public ModelInfo Model => _model.Info;

    /// <summary>
    /// Sends a comment too long for the model to manual review without evaluating it: a person decides, instead of the
    /// comment being dropped or occupying the model for longer than it can answer.
    /// </summary>
    public AnalysisResult SkipTooLong() => new(
        Decision.ManualReview,
        [ReviewReason.TooLong],
        null,
        _model.Info,
        _timeProvider.GetUtcNow(),
        TimeSpan.Zero);

    public async Task<AnalysisResult> AnalyzeAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var startedAt = _timeProvider.GetTimestamp();
        var prediction = await _model.EvaluateAsync(content, cancellationToken).ConfigureAwait(false);
        var duration = _timeProvider.GetElapsedTime(startedAt);
        var decision = _policy.Decide(prediction);

        return new AnalysisResult(
            decision.Decision,
            decision.ReviewReasons,
            prediction,
            _model.Info,
            _timeProvider.GetUtcNow(),
            duration);
    }
}
