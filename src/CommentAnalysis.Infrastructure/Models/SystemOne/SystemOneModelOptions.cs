namespace CommentAnalysis.Infrastructure.Models.SystemOne;

/// <summary>
/// Settings for <see cref="SystemOneDecisionModel"/>: a decision model behind a TypeSafe-compatible
/// <c>POST /v1/systemone</c> API (e.g. a local Basal server, llama.cpp's server or TypeSafe's Jev).
/// </summary>
public sealed class SystemOneModelOptions
{
    /// <summary>Base URL of the API including the version segment, e.g. <c>http://localhost:1234/v1/</c>.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Optional; sent as a bearer token.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The model the server serves, e.g. <c>basal-2.0-mini</c>. A response from another model is an error.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Pinned version of the model's weights. The API does not expose it, so update it whenever the model is replaced
    /// under the same name.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Bounds one request, which asks all questions. A local model on a CPU took 76–90 s for the four questions.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}
