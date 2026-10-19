using CommentAnalysis.Core.Analysis;

namespace CommentAnalysis.Core.Tests.Fakes;

/// <summary>An in-memory <see cref="IAnalysisStore"/> with the same uniqueness rule as the database.</summary>
internal sealed class InMemoryAnalysisStore : IAnalysisStore
{
    private readonly Dictionary<(Guid CommentId, string ModelVersion), AnalysisResult> _rows = [];

    public IReadOnlyDictionary<(Guid CommentId, string ModelVersion), AnalysisResult> Rows => _rows;

    /// <summary>Simulates another worker saving the same comment between the existence check and the save.</summary>
    public bool LoseNextSaveRace { get; set; }

    public Task<bool> ExistsAsync(Guid commentId, string modelVersion, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.ContainsKey((commentId, modelVersion)));

    public Task<SaveOutcome> SaveAsync(Guid commentId, Guid postId, AnalysisResult result, CancellationToken cancellationToken)
    {
        if (LoseNextSaveRace)
        {
            LoseNextSaveRace = false;
            return Task.FromResult(SaveOutcome.AlreadyExists);
        }

        return Task.FromResult(_rows.TryAdd((commentId, result.Model.Version), result)
            ? SaveOutcome.Saved
            : SaveOutcome.AlreadyExists);
    }
}
