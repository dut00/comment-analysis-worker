using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.Infrastructure.Persistence;

/// <summary>
/// Applies pending migrations when <see cref="DatabaseOptions.MigrateOnStartup"/> is enabled.
/// Registered before the message consumers, so they never run against an outdated schema.
/// EF Core logs which migrations it applies (or that the database is up to date).
/// </summary>
internal sealed class DatabaseMigrator(IServiceScopeFactory scopeFactory, IOptions<DatabaseOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.MigrateOnStartup)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AnalysisDbContext>();

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
