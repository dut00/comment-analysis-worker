using Microsoft.Extensions.Options;

namespace CommentAnalysis.Worker.Consumers;

internal sealed class ConsumerOptionsValidator : IValidateOptions<ConsumerOptions>
{
    public ValidateOptionsResult Validate(string? name, ConsumerOptions options)
    {
        var errors = options.GetValidationErrors();

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
