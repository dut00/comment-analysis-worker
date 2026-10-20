namespace CommentAnalysis.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Npgsql connection string to the worker's own database. Outside Development it must verify the server's
    /// certificate and name (<c>SSL Mode=VerifyFull</c>), unless <see cref="AllowPlaintext"/> is set.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Allows a connection without verified TLS outside Development. Only for a database on the same host or private
    /// network, e.g. compose; never in production.
    /// </summary>
    public bool AllowPlaintext { get; set; }

    /// <summary>
    /// Applies pending EF Core migrations at startup. Meant for local development and compose;
    /// production should run a migration bundle before deploying.
    /// </summary>
    public bool MigrateOnStartup { get; set; }
}
