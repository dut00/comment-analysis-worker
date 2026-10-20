using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace CommentAnalysis.Worker.Diagnostics;

internal static class TelemetryExtensions
{
    /// <summary>The standard OpenTelemetry setting; the exporter also reads the other <c>OTEL_*</c> settings.</summary>
    public const string OtlpEndpointSetting = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The standard metrics-only endpoint, which takes precedence over <see cref="OtlpEndpointSetting"/>.</summary>
    public const string OtlpMetricsEndpointSetting = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";

    public const string ServiceNameSetting = "OTEL_SERVICE_NAME";

    /// <summary>The service name unless <see cref="ServiceNameSetting"/> sets another.</summary>
    public const string ServiceName = "comment-analysis-worker";

    /// <summary>
    /// The worker's meter and the platform's built-in ones: the runtime (GC, thread pool), outgoing HTTP (the
    /// model), incoming HTTP (health checks) and Npgsql (connection pool, command duration).
    /// </summary>
    internal static readonly string[] Meters =
    [
        WorkerMetrics.MeterName,
        "System.Runtime",
        "System.Net.Http",
        "Microsoft.AspNetCore.Hosting",
        "Npgsql",
    ];

    /// <summary>
    /// Exports metrics over OTLP when <see cref="OtlpEndpointSetting"/> or <see cref="OtlpMetricsEndpointSetting"/> is
    /// set (environment variable or any other configuration source); without them nothing is exported and the
    /// metrics only exist in-process.
    /// </summary>
    public static IServiceCollection AddCommentAnalysisTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var endpoints = new[] { OtlpEndpointSetting, OtlpMetricsEndpointSetting }
            .Where(setting => !string.IsNullOrWhiteSpace(configuration[setting]))
            .ToList();
        if (endpoints.Count == 0)
        {
            return services;
        }

        // Fails the start instead of exporting nowhere. The value is not echoed: it may carry credentials.
        foreach (var setting in endpoints)
        {
            if (!Uri.TryCreate(configuration[setting], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException($"{setting} must be an absolute http(s) URI.");
            }
        }

        var serviceName = configuration[ServiceNameSetting];
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(string.IsNullOrWhiteSpace(serviceName) ? ServiceName : serviceName))
            .WithMetrics(metrics => metrics.AddMeter(Meters).AddOtlpExporter());

        return services;
    }
}
