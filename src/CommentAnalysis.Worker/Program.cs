using CommentAnalysis.Infrastructure;
using CommentAnalysis.Infrastructure.Messaging;
using CommentAnalysis.Infrastructure.Persistence;
using CommentAnalysis.Worker;
using CommentAnalysis.Worker.Consumers;
using CommentAnalysis.Worker.Outbox;
using Microsoft.Extensions.Options;

if (HealthCheckCommand.IsRequested(args))
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// Graceful shutdown: in-flight messages get up to Host:ShutdownTimeout to finish. Keep it below the
// orchestrator's grace period (compose stop_grace_period, k8s terminationGracePeriodSeconds).
builder.Services.Configure<HostOptions>(builder.Configuration.GetSection("Host"));

builder.Services.AddCommentAnalysis(builder.Configuration);
builder.Services.AddCommentAnalysisPersistence(builder.Configuration);
builder.Services.AddCommentAnalysisMessaging(builder.Configuration);

builder.Services.AddOptions<ConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ConsumerOptions>, ConsumerOptionsValidator>();

// Registered after persistence and messaging, so migrations run before consuming starts.
builder.Services.AddSingleton<CommentCreatedConsumer>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<CommentCreatedConsumer>());

builder.Services.AddOptions<OutboxOptions>()
    .Bind(builder.Configuration.GetSection(OutboxOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OutboxOptions>, OutboxOptionsValidator>();
builder.Services.AddHostedService<OutboxPublisher>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AnalysisDbContext>("postgres", tags: [HealthCheckTags.Ready])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: [HealthCheckTags.Ready])
    .AddCheck<ConsumerHealthCheck>("consumer", tags: [HealthCheckTags.Ready]);

var app = builder.Build();

// Liveness only reports that the process is up and serving requests (no dependency checks),
// so an outage of a dependency does not get the container restarted.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });

// Readiness reports whether the dependencies are reachable; returns 503 when any check fails.
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains(HealthCheckTags.Ready) });

// Invalid configuration (ValidateOnStart) makes RunAsync throw: the host logs "Hosting failed to start"
// and the process exits with a non-zero code. It is deliberately not caught here, because
// WebApplicationFactory runs this entry point and relies on the exception surfacing.
await app.RunAsync();

return 0;
