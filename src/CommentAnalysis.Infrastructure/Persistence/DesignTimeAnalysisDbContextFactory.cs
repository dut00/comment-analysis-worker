using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CommentAnalysis.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> so migrations can be created without starting the worker host.
/// The connection string is used only by commands that talk to a database (e.g. <c>database update</c>).
/// </summary>
internal sealed class DesignTimeAnalysisDbContextFactory : IDesignTimeDbContextFactory<AnalysisDbContext>
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5433;Database=comment_analysis;Username=postgres;Password=postgres";

    public AnalysisDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("Database__ConnectionString") ?? DefaultConnectionString;

        var options = new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseAnalysisDatabase(connectionString)
            .Options;

        return new AnalysisDbContext(options);
    }
}
