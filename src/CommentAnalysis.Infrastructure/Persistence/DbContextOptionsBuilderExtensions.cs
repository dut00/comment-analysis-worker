using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CommentAnalysis.Infrastructure.Persistence;

public static class DbContextOptionsBuilderExtensions
{
    /// <summary>Npgsql settings shared by the host, design-time tooling and tests.</summary>
    public static DbContextOptionsBuilder UseAnalysisDatabase(this DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.UseNpgsql(
            WithDefaults(connectionString),
            npgsql => npgsql.MigrationsHistoryTable(AnalysisDbContext.MigrationsHistoryTable));
    }

    public static DbContextOptionsBuilder<TContext> UseAnalysisDatabase<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseAnalysisDatabase((DbContextOptionsBuilder)builder, connectionString);

    /// <summary>
    /// Turns off GSS (Kerberos) encryption unless the connection string or <c>PGGSSENCMODE</c> sets it: Npgsql prefers
    /// it by default, and the attempt makes the runtime print "Cannot load library libgssapi_krb5.so.2" in the
    /// container image, which has no Kerberos library. TLS is unaffected. GSS encryption needs the Kerberos library
    /// and an explicit mode.
    /// </summary>
    internal static string WithDefaults(string connectionString) =>
        WithDefaults(connectionString, Environment.GetEnvironmentVariable("PGGSSENCMODE"));

    internal static string WithDefaults(string connectionString, string? gssEncModeVariable)
    {
        if (!string.IsNullOrEmpty(gssEncModeVariable))
        {
            return connectionString;
        }

        // NpgsqlConnectionStringBuilder reports every known keyword as present, so look at the keys actually given
        // ("GSS Encryption Mode" or "GssEncryptionMode", in any case).
        var given = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var isSet = given.Keys.Cast<string>().Any(key =>
            key.Replace(" ", "", StringComparison.Ordinal).Equals(nameof(GssEncryptionMode), StringComparison.OrdinalIgnoreCase));
        if (isSet)
        {
            return connectionString;
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable }.ConnectionString;
    }
}
