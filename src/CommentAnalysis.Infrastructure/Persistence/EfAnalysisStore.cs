using CommentAnalysis.Core.Analysis;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CommentAnalysis.Infrastructure.Persistence;

internal sealed class EfAnalysisStore(AnalysisDbContext context) : IAnalysisStore
{
    public Task<bool> ExistsAsync(Guid commentId, string modelVersion, CancellationToken cancellationToken) =>
        context.CommentAnalyses
            .AsNoTracking()
            .AnyAsync(a => a.CommentId == commentId && a.ModelVersion == modelVersion, cancellationToken);

    public async Task<SaveOutcome> SaveAsync(
        Guid commentId,
        Guid postId,
        AnalysisResult result,
        CancellationToken cancellationToken)
    {
        var entity = CommentAnalysisEntity.Create(commentId, postId, result);
        context.CommentAnalyses.Add(entity);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return SaveOutcome.Saved;
        }
        catch (DbUpdateException exception) when (IsDuplicateAnalysis(exception))
        {
            // Another worker (or a redelivery) stored this comment and model version in the meantime.
            context.Entry(entity).State = EntityState.Detached;
            return SaveOutcome.AlreadyExists;
        }
    }

    private static bool IsDuplicateAnalysis(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: CommentAnalysisEntity.UniqueCommentModelVersionIndex,
        };
}
