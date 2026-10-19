namespace CommentAnalysis.Worker;

/// <summary>
/// Lets the container probe its own liveness endpoint (<c>dotnet CommentAnalysis.Worker.dll healthcheck</c>)
/// without shipping curl or wget in the runtime image.
/// </summary>
internal static class HealthCheckCommand
{
    public const string Name = "healthcheck";

    private const string DefaultPort = "8080";

    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Name, StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0].Trim();
        var uri = new Uri($"http://localhost:{(string.IsNullOrEmpty(port) ? DefaultPort : port)}/health/live");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(uri).ConfigureAwait(false);

            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Health check against {uri} failed: {exception.Message}").ConfigureAwait(false);

            return 1;
        }
    }
}
