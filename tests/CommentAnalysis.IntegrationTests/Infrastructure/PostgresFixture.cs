using CommentAnalysis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;

namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL container per test run (see <see cref="ContainersTestGroup"/>), migrated to the latest schema.
/// Tests isolate their data by using fresh comment ids instead of cleaning tables.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // The same version as docker-compose.yml; update both together.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.11").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public AnalysisDbContext CreateContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<AnalysisDbContext>()
            .UseAnalysisDatabase(ConnectionString)
            .AddInterceptors(interceptors)
            .Options);
}
