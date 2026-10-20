using CommentAnalysis.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CommentAnalysis.Infrastructure.Options;

/// <summary>
/// Requires a connection string, and outside Development one that verifies the server (<c>SSL Mode=VerifyFull</c>):
/// comments and the database password must not travel in clear text.
/// </summary>
internal sealed class DatabaseOptionsValidator(IHostEnvironment environment) : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        const string section = DatabaseOptions.SectionName;

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return ValidateOptionsResult.Fail($"{section}:ConnectionString is required.");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        }
        catch (ArgumentException)
        {
            // The message could quote the connection string, password included.
            return ValidateOptionsResult.Fail($"{section}:ConnectionString is not a valid connection string.");
        }

        if (builder.SslMode != SslMode.VerifyFull && !options.AllowPlaintext && !environment.IsDevelopment())
        {
            return ValidateOptionsResult.Fail(
                $"{section}:ConnectionString must use SSL Mode=VerifyFull outside Development (or set {section}:AllowPlaintext for a database on a private network).");
        }

        return ValidateOptionsResult.Success;
    }
}
