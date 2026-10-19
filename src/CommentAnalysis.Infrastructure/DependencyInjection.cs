using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Messaging;
using CommentAnalysis.Infrastructure.Models;
using CommentAnalysis.Infrastructure.Options;
using CommentAnalysis.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.Infrastructure;

public static partial class DependencyInjection
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
        services.AddHostedService<DecisionModelWarmup>();

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

        services.AddScoped<IAnalysisStore, EfAnalysisStore>();
        services.AddHostedService<DatabaseMigrator>();

        return services;
    }

    /// <summary>
    /// Registers the shared RabbitMQ connection and the background topology declaration.
    /// The host starts even when RabbitMQ is unavailable; see <see cref="RabbitMqTopologyInitializer"/>.
    /// </summary>
    public static IServiceCollection AddCommentAnalysisMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RabbitMqOptions>, RabbitMqOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<RabbitMqTopologyInitializer>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RabbitMqTopologyInitializer>());

        return services;
    }

    private static IDecisionModel CreateDecisionModel(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<ModelOptions>>().Value;

        switch (options.Provider)
        {
            case ModelProvider.Static:
                if (serviceProvider.GetService<IHostEnvironment>() is { } environment && !environment.IsDevelopment())
                {
                    LogStaticModelOutsideDevelopment(
                        serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DependencyInjection)),
                        environment.EnvironmentName,
                        options.Static.Label);
                }

                return new StaticDecisionModel(options.Static);

            default:
                throw new InvalidOperationException($"Unsupported model provider '{options.Provider}'.");
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "The Static model is active in the {Environment} environment: every comment is labelled {Label} without analysis. Use it for local development and tests only.")]
    private static partial void LogStaticModelOutsideDevelopment(ILogger logger, string environment, ContentLabel label);
}
