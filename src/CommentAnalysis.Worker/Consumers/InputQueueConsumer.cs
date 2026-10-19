using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CommentAnalysis.Worker.Consumers;

/// <summary>
/// An <see cref="AsyncEventingBasicConsumer"/> that reports a broker-side cancel (<c>basic.cancel</c> sent by
/// RabbitMQ, e.g. because the queue was deleted). Unlike the unregistered event, this is not raised for our
/// own cancel or for a connection failure, which automatic recovery handles.
/// </summary>
internal sealed class InputQueueConsumer(IChannel channel, Action onCancelledByBroker) : AsyncEventingBasicConsumer(channel)
{
    public override async Task HandleBasicCancelAsync(string consumerTag, CancellationToken cancellationToken = default)
    {
        onCancelledByBroker();
        await base.HandleBasicCancelAsync(consumerTag, cancellationToken).ConfigureAwait(false);
    }
}
