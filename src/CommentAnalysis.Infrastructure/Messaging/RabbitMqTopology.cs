using RabbitMQ.Client;

namespace CommentAnalysis.Infrastructure.Messaging;

/// <summary>
/// Declares the exchanges, queues and bindings the worker relies on. All declarations are
/// idempotent, so every worker instance runs them at startup.
/// </summary>
/// <remarks>
/// Queue arguments cannot be changed on an existing queue (RabbitMQ answers PRECONDITION_FAILED).
/// Changing them requires deleting the queue or moving the setting to a RabbitMQ policy.
/// </remarks>
internal static class RabbitMqTopology
{
    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            options.Exchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            options.DeadLetterExchange,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            options.DeadLetterQueue,
            options.DeadLetterExchange,
            routingKey: options.InputQueue,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Quorum queue: replicated and durable. Rejected messages, and messages delivered more than
        // DeliveryLimit times, are dead-lettered to the DLQ (routed by the input queue's name).
        // At-least-once dead-lettering keeps a message in the input queue until the DLQ has confirmed it,
        // instead of dropping it when the DLQ is unavailable; RabbitMQ requires reject-publish overflow for it.
        await channel.QueueDeclareAsync(
            options.InputQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = options.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = options.InputQueue,
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
                ["x-delivery-limit"] = options.DeliveryLimit,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            options.InputQueue,
            options.Exchange,
            routingKey: options.InputRoutingKey,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
