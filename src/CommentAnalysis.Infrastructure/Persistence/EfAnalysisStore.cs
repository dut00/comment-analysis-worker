using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Messages;
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
        // One SaveChanges is one transaction: the event is stored if and only if the analysis is.
        var entity = CommentAnalysisEntity.Create(commentId, postId, result);
        var message = OutboxMessageEntity.Create(
            CommentAnalyzed.MessageType, CommentAnalyzed.From(commentId, postId, result), result.AnalyzedAt);
        context.CommentAnalyses.Add(entity);
        context.OutboxMessages.Add(message);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return SaveOutcome.Saved;
        }
        catch (DbUpdateException exception) when (IsDuplicateAnalysis(exception))
        {
            // Another worker (or a redelivery) stored this comment and model version in the meantime.
            context.Entry(entity).State = EntityState.Detached;
            context.Entry(message).State = EntityState.Detached;
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
