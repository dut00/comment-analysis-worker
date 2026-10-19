using CommentAnalysis.Infrastructure.Models;
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
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
