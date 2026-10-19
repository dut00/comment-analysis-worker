using CommentAnalysis.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CommentAnalysis.Infrastructure.Models;

/// <summary>
/// Creates the decision model at startup instead of on the first message, so problems with it
/// (and warnings such as the Static model running outside Development) surface immediately.
/// </summary>
internal sealed class DecisionModelWarmup(IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        serviceProvider.GetRequiredService<IDecisionModel>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
