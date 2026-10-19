using CommentAnalysis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CommentAnalysis.IntegrationTests.Persistence;

public class MigrationsTests
{
    [Fact]
    public void Model_HasNoChangesMissingFromMigrations()
    {
        // No database connection is opened: this compares the EF model with the latest migration snapshot.
        var options = new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseAnalysisDatabase("Host=localhost;Database=unused")
            .Options;
        using var context = new AnalysisDbContext(options);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The EF model has changes that are not in a migration. Run: dotnet ef migrations add <Name> " +
            "--project src/CommentAnalysis.Infrastructure --startup-project src/CommentAnalysis.Infrastructure " +
            "--output-dir Persistence/Migrations");
    }
}
