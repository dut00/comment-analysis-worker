using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Infrastructure.Models.Llm;

/// <summary>
/// Classifies comments with a chat model served through an OpenAI-compatible API (<c>POST chat/completions</c>),
/// e.g. a local model in LM Studio. The answer is constrained by a JSON schema to a label and a confidence of 0–100.
/// </summary>
/// <remarks>
/// The confidence is what the model reports about itself, not a calibrated probability, so the decision thresholds
/// have to be tuned for the model. Servers may enforce only part of the schema (LM Studio ignores numeric ranges),
/// so the answer is validated here, and anything unexpected is an <see cref="LlmResponseException"/>.
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

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmModelOptions _options;
    private readonly Uri _chatCompletionsUri;

    public LlmDecisionModel(IHttpClientFactory httpClientFactory, LlmModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BaseUrl);

        _httpClientFactory = httpClientFactory;
        _options = options;

        // Without the trailing slash, a relative URI would replace the last segment (e.g. "v1").
        var baseUrl = options.BaseUrl.AbsoluteUri.EndsWith('/') ? options.BaseUrl : new Uri(options.BaseUrl.AbsoluteUri + "/");
        _chatCompletionsUri = new Uri(baseUrl, "chat/completions");

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

        using var request = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
        {
            Content = JsonContent.Create(CreateRequest(content), options: SerializerOptions),
        };
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    private ChatCompletionRequest CreateRequest(string content) => new(
        _options.Model,
        [new("system", _options.Instructions), new("user", FrameComment(content))],
        Temperature: 0,
        _options.MaxTokens,
        new ResponseFormat("json_schema", new JsonSchemaFormat("moderation", Strict: true, JsonNode.Parse(AnswerSchema)!)));

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

        // A different model would be stored under the wrong version.
        if (!string.Equals(completion.Model, _options.Model, StringComparison.Ordinal))
        {
            throw new LlmResponseException(
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
}
