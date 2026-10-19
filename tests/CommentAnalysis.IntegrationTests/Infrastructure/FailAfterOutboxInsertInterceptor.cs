using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>
/// Appends a failing statement to the command that inserts outbox messages, so the database fails after
/// executing every insert of the batch – to prove the inserts are rolled back together.
/// </summary>
internal sealed class FailAfterOutboxInsertInterceptor : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Break(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Break(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private static void Break(DbCommand command)
    {
        if (command.CommandText.Contains("INSERT INTO outbox_messages", StringComparison.Ordinal))
        {
            command.CommandText += ";\nSELECT 1 / 0;";
        }
    }
}
