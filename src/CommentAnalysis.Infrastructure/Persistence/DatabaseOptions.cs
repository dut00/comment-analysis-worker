namespace CommentAnalysis.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Npgsql connection string to the worker's own database.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Applies pending EF Core migrations at startup. Meant for local development and compose;
    /// production should run a migration bundle before deploying.
    /// </summary>
    public bool MigrateOnStartup { get; set; }
}
