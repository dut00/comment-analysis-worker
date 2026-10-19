using CommentAnalysis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CommentAnalysis.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL container per test collection, migrated to the latest schema.
/// Tests isolate their data by using fresh comment ids instead of cleaning tables.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public AnalysisDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AnalysisDbContext>().UseAnalysisDatabase(ConnectionString).Options);
}

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
