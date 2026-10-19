using CommentAnalysis.Core.Messages;
using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Analysis;

/// <summary>
/// Processes one incoming comment: skips it when the current model version already analyzed it,
/// otherwise analyzes and stores it. Safe to run again for the same comment (redelivery, competing workers).
/// </summary>
public sealed class CommentProcessor(CommentAnalyzer analyzer, IAnalysisStore store)
{
    public async Task<ProcessingResult> ProcessAsync(CommentCreated comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comment);

        var model = analyzer.Model;
        if (await store.ExistsAsync(comment.CommentId, model.Version, cancellationToken).ConfigureAwait(false))
        {
            return ProcessingResult.AlreadyAnalyzed(model);
        }

        var analysis = await analyzer.AnalyzeAsync(comment.Content, cancellationToken).ConfigureAwait(false);
        var outcome = await store.SaveAsync(comment.CommentId, comment.PostId, analysis, cancellationToken)
            .ConfigureAwait(false);

        // Another worker may have stored the same comment between the check and the save.
        return outcome == SaveOutcome.Saved
            ? ProcessingResult.Analyzed(analysis)
            : ProcessingResult.AlreadyAnalyzed(model);
    }
}

public enum ProcessingStatus
{
    Analyzed,
    AlreadyAnalyzed,
}

public sealed record ProcessingResult(ProcessingStatus Status, ModelInfo Model, AnalysisResult? Analysis)
{
    public static ProcessingResult Analyzed(AnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return new ProcessingResult(ProcessingStatus.Analyzed, analysis.Model, analysis);
    }

    public static ProcessingResult AlreadyAnalyzed(ModelInfo model) => new(ProcessingStatus.AlreadyAnalyzed, model, null);
}
