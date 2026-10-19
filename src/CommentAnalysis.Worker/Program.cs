using CommentAnalysis.Infrastructure;
using CommentAnalysis.Worker;

if (HealthCheckCommand.IsRequested(args))
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCommentAnalysis(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

// Liveness only reports that the process is up and serving requests; dependency checks
// belong to readiness (/health/ready), added together with the dependencies.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });

// Invalid configuration (ValidateOnStart) makes RunAsync throw: the host logs "Hosting failed to start"
// and the process exits with a non-zero code. It is deliberately not caught here, because
// WebApplicationFactory runs this entry point and relies on the exception surfacing.
await app.RunAsync();

return 0;
