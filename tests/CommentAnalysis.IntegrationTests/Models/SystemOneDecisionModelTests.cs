using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Models.SystemOne;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace CommentAnalysis.IntegrationTests.Models;

public class SystemOneDecisionModelTests
{
    private const string Comment = "Jesteś idiotą i nie masz pojęcia o czym piszesz.";

    /// <summary>The Basal server's model list.</summary>
    private const string BasalModels = """{"models":[{"name":"basal-2.0-mini","mode":"eager"}]}""";

    /// <summary>OpenRouter's description of Jev, trimmed.</summary>
    private const string JevDescription =
        """{"data":{"id":"typesafe/jev-1.14","architecture":{"output_modalities":["decisions"]},"endpoints":[{"name":"TypeSafe | typesafe/jev-1.14-20261014","status":0}]}}""";

    /// <summary>OpenRouter's model list: text models only.</summary>
    private const string OpenRouterModels = """{"data":[{"id":"typesafe/jev-router","architecture":{"output_modalities":["text"]}}]}""";

    private static readonly Action<SystemOneModelOptions> JevOnOpenRouter = options =>
    {
        options.BaseUrl = new Uri("https://openrouter.ai/api/v1/");
        options.Model = "typesafe/jev-1.14-20261014";
        options.Version = "1.14-20261014";
    };

    [Fact]
    public async Task SendsTheCommentWithTheFourQuestions()
    {
        var handler = new StubHandler(Response());
        var model = CreateModel(handler, options => options.ApiKey = "secret");

        await model.EvaluateAsync(Comment, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("http://model.test/v1/systemone"), request.Uri);
        Assert.Equal("Bearer secret", request.Authorization);

        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("basal-2.0-mini", (string?)body["model"]);
        Assert.Equal(Comment, (string?)body["state"]);
        var questions = body["questions"]!.AsObject();
        Assert.Equal(["verdict", "toxicity", "spam", "personal_data"], questions.Select(question => question.Key));
        Assert.Equal("choice", (string?)questions["verdict"]!["type"]);
        Assert.Equal(["approve", "reject"], questions["verdict"]!["criteria"]!.AsObject().Select(option => option.Key));
        Assert.Equal("score", (string?)questions["toxicity"]!["type"]);
        Assert.Equal(4, questions["toxicity"]!["criteria"]!.AsArray().Count);
        Assert.Equal("noul", (string?)questions["spam"]!["type"]);
        Assert.Equal("noul", (string?)questions["personal_data"]!["type"]);
        Assert.All(questions, question => Assert.False(string.IsNullOrWhiteSpace((string?)question.Value!["instructions"])));
    }

    [Fact]
    public async Task OmitsAuthorization_WithoutApiKey()
    {
        var handler = new StubHandler(Response());

        await CreateModel(handler).EvaluateAsync(Comment, CancellationToken.None);

        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task MapsTheAnswers()
    {
        // The answers of the Basal server to an insult with an advert and a phone number.
        var response = Response(
            choice: "reject",
            approve: 0.01017067115753889,
            reject: 0.9898293614387512,
            toxicity: 1.790650486946106,
            spam: 0.9439055919647217,
            personalData: 0.9549623727798462);

        var prediction = await CreateModel(new StubHandler(response)).EvaluateAsync(Comment, CancellationToken.None);

        Assert.Equal(ContentLabel.Unacceptable, prediction.Label);
        Assert.Equal(0.9898293614387512, prediction.Confidence);
        Assert.Equal(0.01017067115753889, prediction.Probabilities["approve"]);
        Assert.Equal(0.9898293614387512, prediction.Probabilities["reject"]);
        Assert.NotNull(prediction.Signals);
        Assert.Equal(1.790650486946106 / 3, prediction.Signals.Toxicity, precision: 12);
        Assert.Equal(0.9439055919647217, prediction.Signals.Spam);
        Assert.Equal(0.9549623727798462, prediction.Signals.PersonalData);
    }

    [Theory]
    [InlineData("approve", ContentLabel.Acceptable)]
    [InlineData("reject", ContentLabel.Unacceptable)]
    public async Task UsesTheProbabilityOfTheChoiceAsTheConfidence(string choice, ContentLabel label)
    {
        var response = choice == "approve" ? Response(choice, approve: 0.7, reject: 0.3) : Response(choice, approve: 0.3, reject: 0.7);

        var prediction = await CreateModel(new StubHandler(response)).EvaluateAsync(Comment, CancellationToken.None);

        Assert.Equal(label, prediction.Label);
        Assert.Equal(0.7, prediction.Confidence);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(3.0, 1.0)]
    public async Task NormalizesTheToxicityScore(double score, double toxicity)
    {
        var prediction = await CreateModel(new StubHandler(Response(toxicity: score))).EvaluateAsync(Comment, CancellationToken.None);

        Assert.Equal(toxicity, prediction.Signals!.Toxicity);
    }

    [Fact]
    public async Task AcceptsAnswersWithoutTheirType()
    {
        // The type is not required by the API; the Basal server sends it.
        var response = Response(edit: answers =>
        {
            foreach (var answer in answers)
            {
                answer.Value!.AsObject().Remove("type");
            }
        });

        var prediction = await CreateModel(new StubHandler(response)).EvaluateAsync(Comment, CancellationToken.None);

        Assert.Equal(ContentLabel.Acceptable, prediction.Label);
    }

    [Theory]
    [MemberData(nameof(InvalidAnswers))]
    public async Task InvalidAnswer_Throws(string response)
    {
        var model = CreateModel(new StubHandler(response));

        await Assert.ThrowsAsync<SystemOneResponseException>(() => model.EvaluateAsync(Comment, CancellationToken.None));
    }

    public static TheoryData<string> InvalidAnswers() =>
    [
        "not json",
        "[]",
        """{"model":"basal-2.0-mini"}""",
        """{"model":"basal-2.0-mini","answers":[]}""",
        Response(edit: answers => answers.Remove("verdict")),
        Response(edit: answers => answers.Remove("toxicity")),
        Response(edit: answers => answers.Remove("spam")),
        Response(edit: answers => answers.Remove("personal_data")),
        Response(choice: "maybe"),
        Response(choice: "Approve"),
        Response(edit: answers => answers["verdict"]!.AsObject().Remove("choice")),
        Response(edit: answers => answers["verdict"]!.AsObject().Remove("probabilities")),
        Response(edit: answers => answers["verdict"]!["probabilities"]!.AsObject().Remove("reject")),
        Response(edit: answers => answers["verdict"]!["probabilities"]!["maybe"] = 0.0),
        Response(approve: 0.9, reject: 0.3),
        Response(approve: 1.2, reject: -0.2),
        Response(choice: "approve", approve: 0.3, reject: 0.7),
        Response(edit: answers => answers["verdict"]!["probabilities"]!["approve"] = "0.9"),
        Response(edit: answers => answers["verdict"]!["type"] = "score"),
        Response(toxicity: 3.01),
        Response(toxicity: -0.1),
        Response(edit: answers => answers["toxicity"]!.AsObject().Remove("score")),
        Response(spam: 1.01),
        Response(personalData: -0.01),
        Response(edit: answers => answers["spam"]!["noul"] = true),
    ];

    [Fact]
    public async Task InvalidAnswer_EchoingTheComment_IsNotInTheException()
    {
        var model = CreateModel(new StubHandler(Response(choice: $"The comment \"{Comment}\" is unacceptable.")));

        var exception = await Assert.ThrowsAsync<SystemOneResponseException>(() => model.EvaluateAsync(Comment, CancellationToken.None));

        Assert.DoesNotContain("idiot", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_MeansTheModelIsUnavailable()
    {
        var model = CreateModel(new StubHandler(new TaskCanceledException("timeout", new TimeoutException())));

        await Assert.ThrowsAsync<ModelUnavailableException>(() => model.EvaluateAsync(Comment, CancellationToken.None));
    }

    [Fact]
    public async Task UnreachableServer_MeansTheModelIsUnavailable()
    {
        var model = CreateModel(new StubHandler(new HttpRequestException("Connection refused")));

        await Assert.ThrowsAsync<ModelUnavailableException>(() => model.EvaluateAsync(Comment, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationByTheCaller_IsNotUnavailability()
    {
        // Only the caller's token means shutdown; the consumer must see it as such.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var model = CreateModel(new StubHandler(new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.EvaluateAsync(Comment, cancelled.Token));
    }

    [Fact]
    public async Task AnswerFromAnotherModel_MeansTheModelIsUnavailable()
    {
        var model = CreateModel(new StubHandler(Response(model: "openjev")));

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => model.EvaluateAsync(Comment, CancellationToken.None));

        Assert.Contains("openjev", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherKindOfServer_MeansTheModelIsUnavailable()
    {
        // LM Studio answers an unknown endpoint with 200 and an error object, without a model.
        var model = CreateModel(new StubHandler("""{"error":"Unexpected endpoint or method. (POST /v1/systemone)"}"""));

        await Assert.ThrowsAsync<ModelUnavailableException>(() => model.EvaluateAsync(Comment, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ErrorStatus_WhileTheServerFailsToo_MeansTheModelIsUnavailable(HttpStatusCode status)
    {
        var model = CreateModel(new StubHandler("""{"error":"model not loaded"}""", status));

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => model.EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(status, Assert.IsType<HttpRequestException>(exception.InnerException).StatusCode);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    public async Task RateLimitOrOverload_MeansTheModelIsUnavailable_WhileTheServerListsTheModel(int status)
    {
        // TypeSafe's rate limit (429) and overload (529) concern the key or the service, never the comment.
        var handler = new StubHandler("""{"detail":{"error_type":"rate_limit_error"}}""", (HttpStatusCode)status)
        {
            ModelsResponse = BasalModels,
            RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)),
        };

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => CreateModel(handler).EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(7), exception.RetryAfter);
        Assert.Equal((HttpStatusCode)status, Assert.IsType<HttpRequestException>(exception.InnerException).StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RateLimit_ReadsRetryAfterAsADate()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler("{}", HttpStatusCode.TooManyRequests)
        {
            RetryAfter = new RetryConditionHeaderValue(time.GetUtcNow().AddSeconds(12)),
        };

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(
            () => CreateModel(handler, timeProvider: time).EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(12), exception.RetryAfter);
    }

    [Fact]
    public async Task RateLimit_RetryAfterInThePast_IsZero()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler("{}", HttpStatusCode.TooManyRequests)
        {
            RetryAfter = new RetryConditionHeaderValue(time.GetUtcNow().AddSeconds(-5)),
        };

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(
            () => CreateModel(handler, timeProvider: time).EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(TimeSpan.Zero, exception.RetryAfter);
    }

    [Fact]
    public async Task RejectedApiKey_MeansTheModelIsUnavailable_EvenIfTheModelListIsPublic()
    {
        var handler = new StubHandler("""{"detail":{"error_type":"authentication_error"}}""", HttpStatusCode.Unauthorized) { ModelsResponse = BasalModels };

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => CreateModel(handler).EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, Assert.IsType<HttpRequestException>(exception.InnerException).StatusCode);
    }

    [Fact]
    public async Task NoCredits_MeansTheModelIsUnavailable()
    {
        // OpenRouter answers 402 when the account has no credits: every comment would fail the same way.
        var handler = new StubHandler("""{"error":{"message":"Insufficient credits","code":402}}""", HttpStatusCode.PaymentRequired) { ModelsResponse = BasalModels };

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => CreateModel(handler).EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(HttpStatusCode.PaymentRequired, Assert.IsType<HttpRequestException>(exception.InnerException).StatusCode);
    }

    [Fact]
    public async Task Health_IsHealthy_WhenOpenRouterDescribesTheModelAsADecisionModel()
    {
        // OpenRouter's model list has only text models; the decision model is described by its endpoints.
        var handler = new StubHandler("{}") { ModelsResponse = OpenRouterModels, EndpointsResponse = JevDescription };

        var result = await CreateModel(handler, JevOnOpenRouter).CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);

        // Only the description: OpenRouter's whole model list is large.
        Assert.Equal(new Uri("https://openrouter.ai/api/v1/models/typesafe/jev-1.14-20261014/endpoints"), Assert.Single(handler.Requests).Uri);
    }

    [Theory]
    [InlineData("""{"data":{"architecture":{"output_modalities":["text"]},"endpoints":[{"status":0}]}}""", HttpStatusCode.OK)]
    [InlineData("""{"data":{"architecture":{"output_modalities":["decisions"]},"endpoints":[]}}""", HttpStatusCode.OK)]
    [InlineData("""{"data":{"architecture":{"output_modalities":["decisions"]},"endpoints":[{"status":-2}]}}""", HttpStatusCode.OK)]
    [InlineData("""{"error":{"message":"Not Found","code":404}}""", HttpStatusCode.NotFound)]
    public async Task Health_IsUnhealthy_WhenOpenRouterDoesNotServeTheDecisionModel(string description, HttpStatusCode status)
    {
        var handler = new StubHandler("{}") { ModelsResponse = OpenRouterModels, EndpointsResponse = description, EndpointsStatus = status };

        var result = await CreateModel(handler, JevOnOpenRouter).CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task ServerError_OnOpenRouter_IsAFailureOfTheComment_WhileTheModelIsServed()
    {
        var handler = new StubHandler("""{"error":{"code":500}}""", HttpStatusCode.InternalServerError) { EndpointsResponse = JevDescription };

        await Assert.ThrowsAsync<SystemOneResponseException>(() => CreateModel(handler, JevOnOpenRouter).EvaluateAsync(Comment, CancellationToken.None));
    }

    [Fact]
    public async Task ServerError_OnOpenRouter_MeansTheModelIsUnavailable_WhenItsProvidersAreDown()
    {
        var handler = new StubHandler("""{"error":{"code":500}}""", HttpStatusCode.InternalServerError)
        {
            ModelsResponse = OpenRouterModels,
            EndpointsResponse = """{"data":{"architecture":{"output_modalities":["decisions"]},"endpoints":[{"status":-5}]}}""",
        };

        await Assert.ThrowsAsync<ModelUnavailableException>(() => CreateModel(handler, JevOnOpenRouter).EvaluateAsync(Comment, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task NoWorkingUpstream_MeansTheModelIsUnavailable_EvenIfTheModelIsListed(HttpStatusCode status)
    {
        // OpenRouter answers 502/503 when its provider is down, while its catalogue still describes the model.
        var handler = new StubHandler("""{"error":{"message":"Provider returned error"}}""", status) { EndpointsResponse = JevDescription };

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(() => CreateModel(handler, JevOnOpenRouter).EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(status, Assert.IsType<HttpRequestException>(exception.InnerException).StatusCode);
    }

    [Fact]
    public async Task Health_StaysHealthy_AfterARateLimit()
    {
        // Throttling is expected under load; it must not take every replica out of readiness.
        var model = CreateModel(new StubHandler("{}", HttpStatusCode.TooManyRequests) { ModelsResponse = BasalModels });
        await Assert.ThrowsAsync<ModelUnavailableException>(() => model.EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(HealthStatus.Healthy, (await model.CheckHealthAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Health_IsUnhealthy_AfterAnOverload()
    {
        var model = CreateModel(new StubHandler("{}", (HttpStatusCode)529) { ModelsResponse = BasalModels });
        await Assert.ThrowsAsync<ModelUnavailableException>(() => model.EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(HealthStatus.Unhealthy, (await model.CheckHealthAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task RateLimit_WithoutRetryAfter_LeavesTheDelayToTheConsumer()
    {
        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(
            () => CreateModel(new StubHandler("{}", HttpStatusCode.TooManyRequests)).EvaluateAsync(Comment, CancellationToken.None));

        Assert.Null(exception.RetryAfter);
    }

    [Fact]
    public async Task NotADecisionModel_MeansTheModelIsUnavailable()
    {
        // llama.cpp answers 501 when the loaded model is not a decision model, and does not list it as one.
        var model = CreateModel(new StubHandler("""{"error":"not a decision model"}""", HttpStatusCode.NotImplemented)
        {
            ModelsResponse = """{"object":"list","data":[{"id":"basal-2.0-mini","architecture":{"output_modalities":["text"]}}]}""",
        });

        await Assert.ThrowsAsync<ModelUnavailableException>(() => model.EvaluateAsync(Comment, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ErrorStatus_WhileTheServerListsTheModel_IsAFailureOfTheComment(HttpStatusCode status)
    {
        // E.g. a server error caused by this comment: counted retries, then the DLQ – not an endless wait.
        var model = CreateModel(new StubHandler("""{"error":"failed to process the state"}""", status) { ModelsResponse = BasalModels });

        var exception = await Assert.ThrowsAsync<SystemOneResponseException>(() => model.EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(status, Assert.IsType<HttpRequestException>(exception.InnerException).StatusCode);
    }

    [Fact]
    public async Task Timeout_WhileTheServerListsTheModel_IsAFailureOfTheComment()
    {
        var model = CreateModel(new StubHandler(new TaskCanceledException("timeout", new TimeoutException())) { ModelsResponse = BasalModels });

        await Assert.ThrowsAsync<SystemOneResponseException>(() => model.EvaluateAsync(Comment, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task InvalidRequest_IsAFailureOfTheComment(HttpStatusCode status)
    {
        // The Basal server answers an invalid request with 422: retried a few times, then dead-lettered.
        var model = CreateModel(new StubHandler("""{"error":"question 'q': 0 options (supported: 2..255)"}""", status));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => model.EvaluateAsync(Comment, CancellationToken.None));

        Assert.Equal(status, exception.StatusCode);
    }

    [Fact]
    public async Task Health_IsUnhealthy_AfterACommentFoundTheModelUnavailable_UntilTheModelAnswersAgain()
    {
        // Another model answering is not visible in the model list, so readiness relies on what comments saw.
        var handler = new StubHandler(Response(model: "openjev")) { ModelsResponse = BasalModels };
        var model = CreateModel(handler);
        Assert.Equal(HealthStatus.Healthy, (await model.CheckHealthAsync(CancellationToken.None)).Status);

        await Assert.ThrowsAsync<ModelUnavailableException>(() => model.EvaluateAsync(Comment, CancellationToken.None));
        var unhealthy = await model.CheckHealthAsync(CancellationToken.None);

        handler.Response = Response();
        await model.EvaluateAsync(Comment, CancellationToken.None);
        var healthy = await model.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.Contains("openjev", unhealthy.Description, StringComparison.Ordinal);
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
    }

    [Fact]
    public async Task Health_ForgetsAnUnavailableModel_AfterAMinute()
    {
        var time = new FakeTimeProvider();
        var model = CreateModel(new StubHandler(Response(model: "openjev")) { ModelsResponse = BasalModels }, timeProvider: time);
        await Assert.ThrowsAsync<ModelUnavailableException>(() => model.EvaluateAsync(Comment, CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(59));
        var stillUnhealthy = await model.CheckHealthAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        var healthy = await model.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, stillUnhealthy.Status);
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
    }

    [Theory]
    [InlineData(BasalModels)]
    [InlineData("""{"object":"list","data":[{"id":"basal-2.0-mini","architecture":{"input_modalities":["text"],"output_modalities":["text","decisions"]}}]}""")]
    public async Task Health_IsHealthy_WhenTheModelIsListed(string models)
    {
        var handler = new StubHandler(models);

        var result = await CreateModel(handler, options => options.ApiKey = "secret").CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("http://model.test/v1/models"), request.Uri);
        Assert.Equal("Bearer secret", request.Authorization);
    }

    [Theory]
    [InlineData("""{"models":[{"name":"openjev"}]}""")]
    [InlineData("""{"models":[]}""")]
    // An OpenAI-style list (e.g. LM Studio) does not mark the model as a decision model.
    [InlineData("""{"object":"list","data":[{"id":"basal-2.0-mini","object":"model"}]}""")]
    [InlineData("""{"object":"list","data":[{"id":"basal-2.0-mini","architecture":{"output_modalities":["text"]}}]}""")]
    [InlineData("""{"object":"list","data":[{"id":"openjev","architecture":{"output_modalities":["decisions"]}}]}""")]
    public async Task Health_IsUnhealthy_WhenTheModelIsNotListedAsADecisionModel(string models)
    {
        var result = await CreateModel(new StubHandler(models)).CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("basal-2.0-mini", result.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"error":"down"}""", HttpStatusCode.ServiceUnavailable)]
    [InlineData("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized)]
    [InlineData("<html>", HttpStatusCode.OK)]
    public async Task Health_IsUnhealthy_OnErrors(string response, HttpStatusCode status)
    {
        var result = await CreateModel(new StubHandler(response, status)).CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Health_IsUnhealthy_WhenTheServerIsUnreachable()
    {
        var model = CreateModel(new StubHandler(new HttpRequestException("Connection refused")));

        var result = await model.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void Version_PinsTheModelVersionAndTheQuestions()
    {
        var model = CreateModel(new StubHandler("{}"));

        Assert.Equal("basal-2.0-mini", model.Info.Name);
        Assert.Matches("^basal-2\\.0-mini@2\\.0\\+[0-9a-f]{12}$", model.Info.Version);
        Assert.Equal(model.Info.Version, CreateModel(new StubHandler("{}")).Info.Version);
    }

    [Fact]
    public void Version_ChangesWithTheQuestions()
    {
        var options = new SystemOneModelOptions { Model = "basal-2.0-mini", Version = "2.0" };
        var changed = SystemOneQuestions.Json.Replace("Polish-speaking", "Polish", StringComparison.Ordinal);

        Assert.NotEqual(SystemOneDecisionModel.CreateVersion(options), SystemOneDecisionModel.CreateVersion(options, changed));
    }

    [Fact]
    public void Request_EscapesTheComment()
    {
        const string comment = "\", \"questions\": {}, \"x\": \"";

        var request = JsonNode.Parse(CreateModel(new StubHandler("{}")).CreateRequest(comment))!;

        Assert.Equal(comment, (string?)request["state"]);
        Assert.Equal(4, request["questions"]!.AsObject().Count);
    }

    [Theory]
    [InlineData("http://model.test/v1")]
    [InlineData("http://model.test/v1/")]
    public async Task BaseUrl_WorksWithAndWithoutTrailingSlash(string baseUrl)
    {
        var handler = new StubHandler(Response());

        await CreateModel(handler, options => options.BaseUrl = new Uri(baseUrl)).EvaluateAsync(Comment, CancellationToken.None);

        Assert.Equal(new Uri("http://model.test/v1/systemone"), Assert.Single(handler.Requests).Uri);
    }

    private static SystemOneDecisionModel CreateModel(
        StubHandler handler,
        Action<SystemOneModelOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        var options = new SystemOneModelOptions
        {
            BaseUrl = new Uri("http://model.test/v1/"),
            Model = "basal-2.0-mini",
            Version = "2.0",
        };
        configure?.Invoke(options);
        return new SystemOneDecisionModel(new StubHttpClientFactory(handler), options, timeProvider);
    }

    /// <summary>A response in the Basal server's shape; <paramref name="edit"/> can change the answers.</summary>
    private static string Response(
        string choice = "approve",
        double approve = 0.95,
        double reject = 0.05,
        double toxicity = 0.5,
        double spam = 0.1,
        double personalData = 0.02,
        string model = "basal-2.0-mini",
        Action<JsonObject>? edit = null)
    {
        var answers = new JsonObject
        {
            ["verdict"] = new JsonObject
            {
                ["type"] = "choice",
                ["choice"] = choice,
                ["probabilities"] = new JsonObject { ["approve"] = approve, ["reject"] = reject },
                ["confidence"] = Math.Max(approve, reject),
            },
            ["toxicity"] = new JsonObject
            {
                ["type"] = "score",
                ["score"] = toxicity,
                ["legend"] = new JsonObject { ["0"] = "none", ["1"] = "mild", ["2"] = "offensive", ["3"] = "severe" },
                ["probabilities"] = new JsonObject { ["0"] = 0.6, ["1"] = 0.3, ["2"] = 0.1, ["3"] = 0.0 },
                ["confidence"] = 0.6,
            },
            ["spam"] = Noul(spam),
            ["personal_data"] = Noul(personalData),
        };
        edit?.Invoke(answers);

        return new JsonObject
        {
            ["model"] = model,
            ["answers"] = answers,
            ["usage"] = new JsonObject { ["input_tokens"] = 685, ["output_tokens"] = 0, ["questions"] = 4 },
        }.ToJsonString();

        static JsonObject Noul(double probability) => new()
        {
            ["type"] = "noul",
            ["noul"] = probability,
            ["probabilities"] = new JsonObject { ["true"] = probability, ["false"] = 1 - probability },
            ["confidence"] = Math.Max(probability, 1 - probability),
        };
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Authorization, string Body);

    /// <summary>
    /// Answers every request with the same response or exception, except <c>GET models</c> when
    /// <see cref="ModelsResponse"/> is set.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status = HttpStatusCode.OK;
        private readonly Exception? _exception;

        public StubHandler(string response, HttpStatusCode status = HttpStatusCode.OK)
        {
            Response = response;
            _status = status;
        }

        public StubHandler(Exception exception) => _exception = exception;

        public string Response { get; set; } = string.Empty;

        /// <summary>The <c>Retry-After</c> header of the response (not of the model list).</summary>
        public RetryConditionHeaderValue? RetryAfter { get; init; }

        /// <summary>OpenRouter's model description (<c>GET models/{id}/endpoints</c>), answered with <see cref="EndpointsStatus"/> if set.</summary>
        public string? EndpointsResponse { get; init; }

        public HttpStatusCode EndpointsStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>The model list, answered with 200 regardless of the other settings.</summary>
        public string? ModelsResponse { get; init; }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (ModelsResponse is not null && request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ModelsResponse, Encoding.UTF8, "application/json") };
            }

            if (EndpointsResponse is not null && request.RequestUri!.AbsolutePath.EndsWith("/endpoints", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(EndpointsStatus) { Content = new StringContent(EndpointsResponse, Encoding.UTF8, "application/json") };
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            var response = new HttpResponseMessage(_status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
            response.Headers.RetryAfter = RetryAfter;
            return response;
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
