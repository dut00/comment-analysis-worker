using Microsoft.EntityFrameworkCore;

namespace CommentAnalysis.Infrastructure.Persistence;

public static class DbContextOptionsBuilderExtensions
{
    /// <summary>Npgsql settings shared by the host, design-time tooling and tests.</summary>
    public static DbContextOptionsBuilder UseAnalysisDatabase(this DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable(AnalysisDbContext.MigrationsHistoryTable));
    }

    public static DbContextOptionsBuilder<TContext> UseAnalysisDatabase<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseAnalysisDatabase((DbContextOptionsBuilder)builder, connectionString);
}
