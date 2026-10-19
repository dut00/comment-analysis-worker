using CommentAnalysis.Core.Decisions;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.Infrastructure.Options;

internal sealed class DecisionPolicyOptionsValidator : IValidateOptions<DecisionPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, DecisionPolicyOptions options)
    {
        var errors = options.GetValidationErrors();

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
