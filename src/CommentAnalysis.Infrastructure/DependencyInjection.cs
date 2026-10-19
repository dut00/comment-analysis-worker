using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Models;
using CommentAnalysis.Infrastructure.Options;
using CommentAnalysis.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the comment analysis pipeline: options (validated at startup), the decision
    /// policy, the configured decision model and the analyzer.
    /// </summary>
    public static IServiceCollection AddCommentAnalysis(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DecisionPolicyOptions>()
            .Bind(configuration.GetSection(DecisionPolicyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DecisionPolicyOptions>, DecisionPolicyOptionsValidator>();

        services.AddOptions<ModelOptions>()
            .Bind(configuration.GetSection(ModelOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ModelOptions>, ModelOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new DecisionPolicy(sp.GetRequiredService<IOptions<DecisionPolicyOptions>>().Value));
        services.AddSingleton(CreateDecisionModel);
        services.AddSingleton<CommentAnalyzer>();

        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL database (EF Core) and, when enabled, migrations at startup.
    /// Call before registering message consumers so migrations run first.
    /// </summary>
    public static IServiceCollection AddCommentAnalysisPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();

        services.AddDbContext<AnalysisDbContext>((serviceProvider, builder) =>
            builder.UseAnalysisDatabase(serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        services.AddHostedService<DatabaseMigrator>();

        return services;
    }

    private static IDecisionModel CreateDecisionModel(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<ModelOptions>>().Value;

        return options.Provider switch
        {
            ModelProvider.Static => new StaticDecisionModel(options.Static),
            _ => throw new InvalidOperationException($"Unsupported model provider '{options.Provider}'."),
        };
    }
}
