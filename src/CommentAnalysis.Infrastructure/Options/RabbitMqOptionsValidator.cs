using CommentAnalysis.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.Infrastructure.Options;

internal sealed class RabbitMqOptionsValidator : IValidateOptions<RabbitMqOptions>
{
    public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
    {
        const string section = RabbitMqOptions.SectionName;
        var errors = new List<string>();

        if (options.Uri is null)
        {
            errors.Add($"{section}:Uri is required.");
        }
        else if (options.Uri.Scheme is not ("amqp" or "amqps"))
        {
            errors.Add($"{section}:Uri must use the amqp or amqps scheme, but was '{options.Uri.Scheme}'.");
        }

        AddErrorIfBlank(errors, nameof(options.ClientName), options.ClientName);
        AddErrorIfBlank(errors, nameof(options.Exchange), options.Exchange);
        AddErrorIfBlank(errors, nameof(options.InputQueue), options.InputQueue);
        AddErrorIfBlank(errors, nameof(options.InputRoutingKey), options.InputRoutingKey);
        AddErrorIfBlank(errors, nameof(options.OutputRoutingKey), options.OutputRoutingKey);
        AddErrorIfBlank(errors, nameof(options.DeadLetterExchange), options.DeadLetterExchange);
        AddErrorIfBlank(errors, nameof(options.DeadLetterQueue), options.DeadLetterQueue);

        if (options.DeliveryLimit < 1)
        {
            errors.Add($"{section}:DeliveryLimit must be at least 1, but was {options.DeliveryLimit}.");
        }

        if (options.MaxReconnectDelay <= TimeSpan.Zero)
        {
            errors.Add($"{section}:MaxReconnectDelay must be positive.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void AddErrorIfBlank(List<string> errors, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{RabbitMqOptions.SectionName}:{name} is required.");
        }
    }
}
