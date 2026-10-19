using CommentAnalysis.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.Infrastructure.Options;

internal sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? ValidateOptionsResult.Fail($"{DatabaseOptions.SectionName}:ConnectionString is required.")
            : ValidateOptionsResult.Success;
}
