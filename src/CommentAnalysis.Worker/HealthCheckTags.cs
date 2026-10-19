namespace CommentAnalysis.Worker;

internal static class HealthCheckTags
{
    /// <summary>Checks of dependencies the worker needs to do its job; reported by /health/ready.</summary>
    public const string Ready = "ready";
}
