using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CommentAnalysis.Core.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CommentAnalysis.Infrastructure.Models.SystemOne;

/// <summary>
/// Evaluates comments with a decision model behind a TypeSafe-compatible <c>POST /v1/systemone</c> API. One request
/// asks the <see cref="SystemOneQuestions"/>: the <c>verdict</c> choice gives the label and its probability the
/// confidence, and the <c>toxicity</c> score and the <c>spam</c> and <c>personal_data</c> yes/no questions give the
/// <see cref="ModerationSignals"/>.
/// </summary>
/// <remarks>
/// The answers come from the model's output distribution, not from generated text, so the model cannot answer
/// outside the options. They are validated anyway, and anything unexpected is a
/// <see cref="SystemOneResponseException"/>. Failures that say nothing about the comment are a
/// <see cref="ModelUnavailableException"/>: an unreachable server, another model (or another kind of server)
/// answering, and a timeout or an error status (3xx/401/403/404/408/429/5xx) unless the server still lists the model.
/// The last check keeps a comment that always fails (e.g. a 500 or a timeout on its long prompt) from waiting forever.
/// Invalid requests are answered with 4xx (422 on the Basal server) and count as failures of the comment.
/// </remarks>
public sealed class SystemOneDecisionModel : IDecisionModel
{
    public const string HttpClientName = "systemone";

    /// <summary>
    /// How the comment is put into the request: as the plain <c>state</c> string. Part of the model version: change it
    /// whenever <see cref="CreateRequest"/> changes the framing.
    /// </summary>
    private const string FramingVersion = "plain-state-v1";

    /// <summary>How far the probabilities of an answer may sum from 1 (they are rounded floats).</summary>
    private const double ProbabilitySumTolerance = 1e-3;

    /// <summary>The output modality of a decision model in a llama.cpp model list.</summary>
    private const string DecisionsModality = "decisions";

    /// <summary>Bounds checking the server, both for readiness and to confirm an outage.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long readiness reports an outage seen by a comment, unless a later comment succeeds.</summary>
    private static readonly TimeSpan RecentUnavailabilityWindow = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SystemOneModelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Uri _systemOneUri;
    private readonly Uri _modelsUri;

    private volatile Unavailability? _lastUnavailability;

    public SystemOneDecisionModel(IHttpClientFactory httpClientFactory, SystemOneModelOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BaseUrl);

        _httpClientFactory = httpClientFactory;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Without the trailing slash, a relative URI would replace the last segment (e.g. "v1").
        var baseUrl = options.BaseUrl.AbsoluteUri.EndsWith('/') ? options.BaseUrl : new Uri(options.BaseUrl.AbsoluteUri + "/");
        _systemOneUri = new Uri(baseUrl, "systemone");
        _modelsUri = new Uri(baseUrl, "models");

        Info = new ModelInfo(options.Model, CreateVersion(options));
    }

    public ModelInfo Info { get; }

    /// <summary>
    /// <c>{Model}@{Version}+{first 12 hex chars of SHA-256 of the framing and the questions}</c>.
    /// </summary>
    internal static string CreateVersion(SystemOneModelOptions options) =>
        CreateVersion(options, SystemOneQuestions.Json);

    internal static string CreateVersion(SystemOneModelOptions options, string questions)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{FramingVersion}\n{questions}"));
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
                throw new SystemOneResponseException(
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
    /// unavailable within the last minute (e.g. another server answering on the same address).
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
        using var request = new HttpRequestMessage(HttpMethod.Post, _systemOneUri)
        {
            Content = new StringContent(CreateRequest(content), Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Anything else than the statuses SendAsync handles (e.g. 422 for a request the server cannot answer)
        // is specific to the request.
        response.EnsureSuccessStatusCode();

        JsonDocument document;
        try
        {
            document = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new SystemOneResponseException("The response is not valid JSON.", exception);
        }

        using (document)
        {
            return ParseResponse(document.RootElement);
        }
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
                .ReadFromJsonAsync<ModelList>(JsonSerializerOptions.Web, timeout.Token)
                .ConfigureAwait(false);
            return ServesModel(models)
                ? (null, null)
                : ($"The model server does not serve '{_options.Model}' as a decision model.", null);
        }
        catch (Exception exception) when (exception is ModelUnavailableException or SuspectedUnavailabilityException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return ("The model server is unavailable.", exception);
        }
    }

    /// <summary>
    /// Accepts the Basal server's list (<c>models[].name</c>) and llama.cpp's (<c>data[].id</c>, where a decision model
    /// reports the <c>decisions</c> output modality; an OpenAI-style server such as LM Studio does not).
    /// </summary>
    private bool ServesModel(ModelList? models) =>
        models?.Models?.Any(model => string.Equals(model.Name, _options.Model, StringComparison.Ordinal)) == true
        || models?.Data?.Any(model => string.Equals(model.Id, _options.Model, StringComparison.Ordinal)
            && model.Architecture?.OutputModalities?.Contains(DecisionsModality, StringComparer.Ordinal) == true) == true;

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
            // The client's timeout, e.g. while the server loads the model or is busy with other requests.
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

    /// <summary>
    /// Statuses that usually say nothing about the comment: redirects (disabled), authorization (e.g. a wrong API key),
    /// a missing endpoint, and server errors (including 501, a model that is not a decision model). A server error
    /// can concern one comment, which the probe tells apart.
    /// </summary>
    private static bool IsSuspectedUnavailableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status is >= 300 and < 400 or >= 500;

    /// <summary>
    /// The Basal server ignores <c>model</c>, but servers with several models (TypeSafe, a llama.cpp router) need it.
    /// </summary>
    internal string CreateRequest(string content) => new JsonObject
    {
        ["model"] = _options.Model,
        ["state"] = content,
        ["questions"] = JsonNode.Parse(SystemOneQuestions.Json),
    }.ToJsonString();

    private ModelPrediction ParseResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SystemOneResponseException("The response is not a JSON object.");
        }

        // A different model would be stored under the wrong version. It is served to every comment (e.g. another
        // model was loaded, or another kind of server answers on the address), so the comments wait until the
        // configured model is back.
        var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString()
            : null;
        if (!string.Equals(model, _options.Model, StringComparison.Ordinal))
        {
            throw new ModelUnavailableException(model is null
                ? $"The response does not name its model, but '{_options.Model}' is configured."
                : $"The response comes from model '{model}', but '{_options.Model}' is configured.");
        }

        if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
        {
            throw new SystemOneResponseException("The response has no answers.");
        }

        var (label, confidence, probabilities) = ParseVerdict(GetAnswer(answers, SystemOneQuestions.Verdict, "choice"));
        var toxicity = GetNumber(GetAnswer(answers, SystemOneQuestions.Toxicity, "score"), "score", SystemOneQuestions.Toxicity, SystemOneQuestions.MaxToxicityLevel);
        var spam = GetNumber(GetAnswer(answers, SystemOneQuestions.Spam, "noul"), "noul", SystemOneQuestions.Spam, 1);
        var personalData = GetNumber(GetAnswer(answers, SystemOneQuestions.PersonalData, "noul"), "noul", SystemOneQuestions.PersonalData, 1);

        return new ModelPrediction(
            label,
            confidence,
            probabilities,
            new ModerationSignals(toxicity / SystemOneQuestions.MaxToxicityLevel, spam, personalData));
    }

    private static JsonElement GetAnswer(JsonElement answers, string question, string type)
    {
        if (!answers.TryGetProperty(question, out var answer) || answer.ValueKind != JsonValueKind.Object)
        {
            throw new SystemOneResponseException($"The response has no answer to '{question}'.");
        }

        // The type is optional in the API; when present it must be the one asked.
        if (answer.TryGetProperty("type", out var typeElement)
            && !(typeElement.ValueKind == JsonValueKind.String && typeElement.ValueEquals(type)))
        {
            throw new SystemOneResponseException($"The answer to '{question}' is not of type '{type}'.");
        }

        return answer;
    }

    /// <summary>
    /// The chosen option, its probability as the confidence, and the probabilities of both options, which must be
    /// valid and make the chosen option the most likely one.
    /// </summary>
    private static (ContentLabel Label, double Confidence, Dictionary<string, double> Probabilities) ParseVerdict(JsonElement answer)
    {
        const string question = SystemOneQuestions.Verdict;

        var choice = answer.TryGetProperty("choice", out var choiceElement) && choiceElement.ValueKind == JsonValueKind.String
            ? choiceElement.GetString()
            : null;
        ContentLabel label = choice switch
        {
            SystemOneQuestions.Approve => ContentLabel.Acceptable,
            SystemOneQuestions.Reject => ContentLabel.Unacceptable,
            _ => throw new SystemOneResponseException(
                $"The answer to '{question}' is not '{SystemOneQuestions.Approve}' or '{SystemOneQuestions.Reject}'."),
        };

        if (!answer.TryGetProperty("probabilities", out var probabilitiesElement) || probabilitiesElement.ValueKind != JsonValueKind.Object)
        {
            throw new SystemOneResponseException($"The answer to '{question}' has no probabilities.");
        }

        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in probabilitiesElement.EnumerateObject())
        {
            if (property.Name is not (SystemOneQuestions.Approve or SystemOneQuestions.Reject) || !probabilities.TryAdd(property.Name, 0))
            {
                throw new SystemOneResponseException($"The answer to '{question}' has probabilities for unexpected options.");
            }

            probabilities[property.Name] = GetNumber(probabilitiesElement, property.Name, question, 1);
        }

        if (probabilities.Count != 2 || Math.Abs(probabilities.Values.Sum() - 1) > ProbabilitySumTolerance)
        {
            throw new SystemOneResponseException($"The probabilities of '{question}' do not cover both options or do not sum to 1.");
        }

        var confidence = probabilities[choice];
        if (probabilities.Values.Any(probability => probability > confidence))
        {
            throw new SystemOneResponseException($"The answer to '{question}' is not its most likely option.");
        }

        return (label, confidence, probabilities);
    }

    private static double GetNumber(JsonElement element, string property, string question, double max)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || number < 0
            || number > max)
        {
            throw new SystemOneResponseException($"The answer to '{question}' has no '{property}' within [0, {max}].");
        }

        return number;
    }

    /// <summary>Both known shapes of a model list: the Basal server's and llama.cpp's (OpenAI-style).</summary>
    private sealed record ModelList(IReadOnlyList<NamedModel>? Models, IReadOnlyList<ListedModel>? Data);

    private sealed record NamedModel(string? Name);

    private sealed record ListedModel(string? Id, ModelArchitecture? Architecture);

    private sealed record ModelArchitecture(
        [property: JsonPropertyName("output_modalities")] IReadOnlyList<string>? OutputModalities);

    private sealed record Unavailability(DateTimeOffset At, string Reason);

    /// <summary>A failure that suggests an outage, to be confirmed by checking the server.</summary>
    private sealed class SuspectedUnavailabilityException(string message, Exception innerException) : Exception(message, innerException);
}
