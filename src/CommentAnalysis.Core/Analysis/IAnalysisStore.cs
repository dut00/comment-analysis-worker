namespace CommentAnalysis.Core.Analysis;

/// <summary>
/// Persists analysis results. A comment is stored at most once per model version.
/// </summary>
public interface IAnalysisStore
{
    Task<bool> ExistsAsync(Guid commentId, string modelVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the result together with the <see cref="Messages.CommentAnalyzed"/> event to publish, atomically:
    /// either both are stored or neither is. Returns <see cref="SaveOutcome.AlreadyExists"/> instead of throwing when
    /// another worker stored the same comment and model version first.
    /// </summary>
    Task<SaveOutcome> SaveAsync(Guid commentId, Guid postId, AnalysisResult result, CancellationToken cancellationToken);
}

public enum SaveOutcome
{
    Saved,
    AlreadyExists,
}
