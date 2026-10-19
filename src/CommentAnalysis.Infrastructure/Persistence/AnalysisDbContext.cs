using Microsoft.EntityFrameworkCore;

namespace CommentAnalysis.Infrastructure.Persistence;

public sealed class AnalysisDbContext(DbContextOptions<AnalysisDbContext> options) : DbContext(options)
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<CommentAnalysisEntity> CommentAnalyses => Set<CommentAnalysisEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AnalysisDbContext).Assembly);
    }
}
