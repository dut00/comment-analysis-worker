using System.Net;
using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Models.Llm;
using CommentAnalysis.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.IntegrationTests.Host;

public class WorkerHostTests
{
    [Fact]
    public async Task LivenessEndpoint_ReturnsOk()
    {
        await using var factory = new WorkerFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Analyzer_UsesStaticModelFromConfiguration()
    {
        await using var factory = new WorkerFactory(
            ("Model:Static:Label", "Unacceptable"),
            ("Model:Static:Confidence", "0.95"),
            ("Model:Static:Version", "2.0.0"));
        var analyzer = factory.Services.GetRequiredService<CommentAnalyzer>();

        var result = await analyzer.AnalyzeAsync("any comment", CancellationToken.None);

        Assert.Equal(Decision.Reject, result.Decision);
        Assert.Equal(0.95, result.Confidence);
        Assert.Equal("static@2.0.0", result.Model.Version);
    }

    [Fact]
    public async Task DecisionModel_IsTheLlm_WhenConfigured()
    {
        await using var factory = new WorkerFactory(
            ("Model:Provider", "Llm"),
            ("Model:Llm:BaseUrl", "http://localhost:1234/v1/"),
            ("Model:Llm:Model", "basal-2.0-mini"),
            ("Model:Llm:Version", "2.0"));

        var model = factory.Services.GetRequiredService<IDecisionModel>();

        Assert.IsType<LlmDecisionModel>(model);
        Assert.StartsWith("basal-2.0-mini@2.0+", model.Info.Version, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_Fails_WhenLlmIsNotConfigured()
    {
        // Production: only the shipped appsettings.json applies, which has no LLM settings.
        await using var factory = new WorkerFactory(("Model:Provider", "Llm")) { Environment = "Production" };

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("Model:Llm:BaseUrl", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Model:Llm:Model is required", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Model:Llm:Version is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_Fails_WhenModelProviderIsNotConfigured_OutsideDevelopment()
    {
        // Only the shipped appsettings.json applies: a deployment that forgets Model__Provider
        // must not fall back to a model that approves everything.
        await using var factory = new WorkerFactory(("Model:Provider", null)) { Environment = "Production" };

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("Model:Provider is required", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DecisionPolicy:ApproveThreshold", "0.3", "DecisionPolicy:ApproveThreshold")]
    [InlineData("Model:Provider", "", "Model:Provider is required")]
    [InlineData("Model:Static:Confidence", "1.5", "Model:Static:Confidence")]
    [InlineData("Database:ConnectionString", "", "Database:ConnectionString is required")]
    public async Task Startup_Fails_WhenConfigurationIsInvalid(string key, string value, string expectedMessage)
    {
        await using var factory = new WorkerFactory((key, value));

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }
}
