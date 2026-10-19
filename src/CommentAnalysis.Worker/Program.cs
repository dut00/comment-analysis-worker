using CommentAnalysis.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCommentAnalysis(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

// Liveness only reports that the process is up and serving requests; dependency checks
// belong to readiness (/health/ready), added together with the dependencies.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });

app.Run();
