using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommentAnalysis.Core.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CommentAnalysis.Infrastructure.Models.Llm;

/// <summary>
/// Classifies comments with a chat model served through an OpenAI-compatible API (<c>POST chat/completions</c>),
/// e.g. a local model in LM Studio. The answer is constrained by a JSON schema to a label and a confidence of 0–100.
/// </summary>
/// <remarks>
/// The confidence is what the model reports about itself, not a calibrated probability, so the decision thresholds
/// have to be tuned for the model. Servers may enforce only part of the schema (LM Studio ignores numeric ranges),
/// so the answer is validated here, and anything unexpected is an <see cref="LlmResponseException"/>. Failures that
/// say nothing about the comment are a <see cref="ModelUnavailableException"/>: an unreachable server, another model
/// answering, and a timeout or an error status (3xx/401/403/404/408/429/5xx) unless the server still lists the model.
/// The last check keeps a comment that always fails (e.g. a 500 or a timeout on its long prompt) from waiting forever.
/// </remarks>
public sealed class LlmDecisionModel : IDecisionModel
{
    public const string HttpClientName = "llm";

    internal const string AcceptableLabel = "acceptable";
    internal const string UnacceptableLabel = "unacceptable";

    private const int MaxConfidence = 100;

    /// <summary>
    /// How the comment is put into the request. Part of the model version: change it whenever <see cref="FrameComment"/>
    /// changes, because the framing affects the answers as much as the instructions do.
    /// </summary>
    private const string FramingVersion = "json-comment-v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>The answer's schema. Part of the model version, like the instructions.</summary>
    private static readonly string AnswerSchema = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["label"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(AcceptableLabel, UnacceptableLabel) },
            ["confidence"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = MaxConfidence },
        },
        ["required"] = new JsonArray("label", "confidence"),
        ["additionalProperties"] = false,
    }.ToJsonString();

    /// <summary>Bounds checking the server, both for readiness and to confirm an outage.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long readiness reports an outage seen by a comment, unless a later comment succeeds.</summary>
    private static readonly TimeSpan RecentUnavailabilityWindow = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmModelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Uri _chatCompletionsUri;
    private readonly Uri _modelsUri;

    private volatile Unavailability? _lastUnavailability;

    public LlmDecisionModel(IHttpClientFactory httpClientFactory, LlmModelOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BaseUrl);

        _httpClientFactory = httpClientFactory;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Without the trailing slash, a relative URI would replace the last segment (e.g. "v1").
        var baseUrl = options.BaseUrl.AbsoluteUri.EndsWith('/') ? options.BaseUrl : new Uri(options.BaseUrl.AbsoluteUri + "/");
        _chatCompletionsUri = new Uri(baseUrl, "chat/completions");
        _modelsUri = new Uri(baseUrl, "models");

        Info = new ModelInfo(options.Model, CreateVersion(options));
    }

    public ModelInfo Info { get; }

    /// <summary>
    /// <c>{Model}@{Version}+{first 12 hex chars of SHA-256 of the framing, the instructions and the answer schema}</c>.
    /// </summary>
    internal static string CreateVersion(LlmModelOptions options)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{FramingVersion}\n{options.Instructions}\n{AnswerSchema}"));
        return $"{options.Model}@{options.Version}+{Convert.ToHexStringLower(hash)[..12]}";
    }

    public async Task<ModelPrediction> EvaluateAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            var prediction = await RequestPredictionAsync(content, cancellationToken).ConfigureAwait(false);
            _lastUnavailability = null;
            return prediction;
        }
        catch (SuspectedUnavailabilityException exception)
        {
            // A timeout or an error status may concern this comment only (e.g. a long prompt, or a server error
            // while processing it). If the server still serves the model, the comment is at fault: it gets the
            // counted retries and then the DLQ, instead of waiting for a model that is not down.
            var (problem, _) = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (problem is null)
            {
                throw new LlmResponseException(
                    $"{exception.Message} The server still serves the model, so the failure is attributed to the comment.",
                    exception.InnerException!);
            }

            throw MarkUnavailable(new ModelUnavailableException(exception.Message, exception.InnerException!));
        }
        catch (ModelUnavailableException exception)
        {
            MarkUnavailable(exception);
            throw;
        }
    }

    /// <summary>
    /// Healthy while the server lists the configured model (<c>GET models</c>) and no comment found the model
    /// unavailable within the last minute (e.g. another model answers, which the list does not show). LM Studio also
    /// lists models that are downloaded but not loaded (it loads them on demand), so this proves the model can be
    /// served, not that it is warm.
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var (problem, exception) = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return HealthCheckResult.Unhealthy(problem, exception);
        }

        if (_lastUnavailability is { } last && _timeProvider.GetUtcNow() - last.At < RecentUnavailabilityWindow)
        {
            return HealthCheckResult.Unhealthy($"The model was unavailable for a comment at {last.At:O}: {last.Reason}");
        }

        return HealthCheckResult.Healthy();
    }

    private async Task<ModelPrediction> RequestPredictionAsync(string content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
        {
            Content = JsonContent.Create(CreateRequest(content), options: SerializerOptions),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Anything else than the statuses SendAsync handles (e.g. 400 for a comment beyond the context window)
        // is specific to the request.
        response.EnsureSuccessStatusCode();

        ChatCompletionResponse? completion;
        try
        {
            completion = await response.Content
                .ReadFromJsonAsync<ChatCompletionResponse>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new LlmResponseException("The response is not a chat completion.", exception);
        }

        return ParseCompletion(completion);
    }

    /// <summary>Checks that the server lists the configured model; returns the problem, or null if there is none.</summary>
    private async Task<(string? Problem, Exception? Exception)> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _modelsUri);
            using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ($"The model server answered {(int)response.StatusCode} to the model list.", null);
            }

            var models = await response.Content
                .ReadFromJsonAsync<ModelList>(SerializerOptions, timeout.Token)
                .ConfigureAwait(false);
            return models?.Data?.Any(model => string.Equals(model.Id, _options.Model, StringComparison.Ordinal)) == true
                ? (null, null)
                : ($"The model server does not serve '{_options.Model}'.", null);
        }
        catch (Exception exception) when (exception is ModelUnavailableException or SuspectedUnavailabilityException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return ("The model server is unavailable.", exception);
        }
    }

    /// <summary>
    /// Sends with the API key. An unreachable server is a <see cref="ModelUnavailableException"/>; a timeout and the
    /// statuses of <see cref="IsSuspectedUnavailableStatus"/> are a <see cref="SuspectedUnavailabilityException"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ModelUnavailableException("The model server is unreachable.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's timeout, e.g. while the server loads the model on demand.
            throw new SuspectedUnavailabilityException("The model server did not answer in time.", exception);
        }

        if (IsSuspectedUnavailableStatus(response.StatusCode))
        {
            response.Dispose();
            throw new SuspectedUnavailabilityException(
                $"The model server answered {(int)response.StatusCode}.",
                new HttpRequestException(null, null, response.StatusCode));
        }

        return response;
    }

    private ModelUnavailableException MarkUnavailable(ModelUnavailableException exception)
    {
        _lastUnavailability = new Unavailability(_timeProvider.GetUtcNow(), exception.Message);
        return exception;
    }

    private ChatCompletionRequest CreateRequest(string content) => new(
        _options.Model,
        [new("system", _options.Instructions), new("user", FrameComment(content))],
        Temperature: 0,
        _options.MaxTokens,
        new ResponseFormat("json_schema", new JsonSchemaFormat("moderation", Strict: true, JsonNode.Parse(AnswerSchema)!)));

    /// <summary>
    /// Statuses that usually say nothing about the comment: redirects (disabled), authorization (e.g. a wrong API key),
    /// a missing model, and server errors. A server error can concern one comment, which the probe tells apart.
    /// </summary>
    private static bool IsSuspectedUnavailableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status is >= 300 and < 400 or >= 500;

    /// <summary>
    /// JSON-encoded, so the comment cannot pass itself off as part of the prompt (e.g. by imitating an answer).
    /// </summary>
    internal static string FrameComment(string content) => new JsonObject { ["comment"] = content }.ToJsonString();

    private ModelPrediction ParseCompletion(ChatCompletionResponse? completion)
    {
        if (completion is null)
        {
            throw new LlmResponseException("The response is empty.");
        }

        // A different model would be stored under the wrong version. It is served to every comment (e.g. another
        // model was loaded), so the comments wait until the configured one is back.
        if (!string.Equals(completion.Model, _options.Model, StringComparison.Ordinal))
        {
            throw new ModelUnavailableException(
                $"The response comes from model '{completion.Model}', but '{_options.Model}' is configured.");
        }

        if (completion.Choices is not [{ Message.Content: { } answer } choice])
        {
            throw new LlmResponseException("The response does not contain exactly one answer.");
        }

        if (choice.FinishReason is not null and not "stop")
        {
            throw new LlmResponseException($"The answer is incomplete (finish reason '{choice.FinishReason}').");
        }

        return ParseAnswer(answer);
    }

    private static ModelPrediction ParseAnswer(string answer)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(answer);
        }
        catch (JsonException exception)
        {
            throw new LlmResponseException("The answer is not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new LlmResponseException("The answer is not a JSON object.");
            }

            var label = root.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String
                ? labelElement.GetString() switch
                {
                    AcceptableLabel => ContentLabel.Acceptable,
                    UnacceptableLabel => ContentLabel.Unacceptable,
                    _ => (ContentLabel?)null,
                }
                : null;
            if (label is null)
            {
                throw new LlmResponseException($"The answer's label is not '{AcceptableLabel}' or '{UnacceptableLabel}'.");
            }

            // Whole numbers only: a fraction (e.g. 0.6) suggests the model used another scale.
            if (!root.TryGetProperty("confidence", out var confidenceElement)
                || confidenceElement.ValueKind != JsonValueKind.Number
                || !confidenceElement.TryGetDecimal(out var confidence)
                || confidence != decimal.Truncate(confidence)
                || confidence is < 0 or > MaxConfidence)
            {
                throw new LlmResponseException($"The answer's confidence is not an integer from 0 to {MaxConfidence}.");
            }

            return new ModelPrediction(label.Value, (double)confidence / MaxConfidence);
        }
    }

    private sealed record ChatCompletionRequest(
        string Model,
        IReadOnlyList<ChatMessage> Messages,
        double Temperature,
        int MaxTokens,
        ResponseFormat ResponseFormat);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ResponseFormat(string Type, JsonSchemaFormat JsonSchema);

    private sealed record JsonSchemaFormat(string Name, bool Strict, JsonNode Schema);

    private sealed record ChatCompletionResponse(string? Model, IReadOnlyList<ChatChoice>? Choices);

    private sealed record ChatChoice(ChatAnswer? Message, string? FinishReason);

    private sealed record ChatAnswer(string? Content);

    private sealed record ModelList(IReadOnlyList<ModelEntry>? Data);

    private sealed record ModelEntry(string? Id);

    private sealed record Unavailability(DateTimeOffset At, string Reason);

    /// <summary>A failure that suggests an outage, to be confirmed by checking the server.</summary>
    private sealed class SuspectedUnavailabilityException(string message, Exception innerException) : Exception(message, innerException);
}
