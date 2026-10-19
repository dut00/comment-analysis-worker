using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Models;
using CommentAnalysis.Infrastructure.Models.Llm;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.Infrastructure.Options;

internal sealed class ModelOptionsValidator : IValidateOptions<ModelOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelOptions options)
    {
        var errors = new List<string>();

        switch (options.Provider)
        {
            case null:
                errors.Add(
                    $"{ModelOptions.SectionName}:Provider is required. Allowed values: {string.Join(", ", Enum.GetNames<ModelProvider>())}.");
                break;

            case ModelProvider.Static:
                var staticOptions = options.Static;
                const string section = $"{ModelOptions.SectionName}:{nameof(ModelOptions.Static)}";

                if (string.IsNullOrWhiteSpace(staticOptions.Name))
                {
                    errors.Add($"{section}:Name is required.");
                }

                if (string.IsNullOrWhiteSpace(staticOptions.Version))
                {
                    errors.Add($"{section}:Version is required.");
                }

                if (!double.IsFinite(staticOptions.Confidence) || staticOptions.Confidence is < 0 or > 1)
                {
                    errors.Add($"{section}:Confidence must be within [0, 1], but was {staticOptions.Confidence}.");
                }

                break;

            case ModelProvider.Llm:
                ValidateLlm(options.Llm, errors);
                break;
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>The machine itself, or the Docker host as seen from a container.</summary>
    private static bool IsLocal(Uri url) =>
        url.IsLoopback || string.Equals(url.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase);

    private static void ValidateLlm(LlmModelOptions options, List<string> errors)
    {
        const string section = $"{ModelOptions.SectionName}:{nameof(ModelOptions.Llm)}";

        if (options.BaseUrl is not { IsAbsoluteUri: true } baseUrl || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{section}:BaseUrl must be an absolute http(s) URL, e.g. http://localhost:1234/v1/.");
        }
        else if (!string.IsNullOrEmpty(options.ApiKey) && baseUrl.Scheme == Uri.UriSchemeHttp && !IsLocal(baseUrl))
        {
            errors.Add($"{section}:BaseUrl must use https when an ApiKey is set, unless the server is local.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            errors.Add($"{section}:Model is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Version))
        {
            errors.Add($"{section}:Version is required.");
        }

        // The full version is "{Model}@{Version}+{12 hex chars}" and must fit the database column.
        if (options.Model.Length + options.Version.Length + 14 > ModelInfo.MaxLength)
        {
            errors.Add($"{section}:Model and Version together must be at most {ModelInfo.MaxLength - 14} characters.");
        }

        if (string.IsNullOrWhiteSpace(options.Instructions))
        {
            errors.Add($"{section}:Instructions are required.");
        }

        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(10))
        {
            errors.Add($"{section}:Timeout must be within (0, 10 min], but was {options.Timeout}.");
        }

        if (options.MaxTokens is < 16 or > 4096)
        {
            errors.Add($"{section}:MaxTokens must be within [16, 4096], but was {options.MaxTokens}.");
        }
    }
}
