using System.Net;
using CommentAnalysis.Core.Analysis;
using CommentAnalysis.Core.Decisions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CommentAnalysis.IntegrationTests.Host;

public class WorkerHostTests
{
    [Fact]
    public async Task LivenessEndpoint_ReturnsOk()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Analyzer_UsesStaticModelFromConfiguration()
    {
        await using var factory = CreateFactory(
            ("Model:Static:Label", "Unacceptable"),
            ("Model:Static:Confidence", "0.95"),
            ("Model:Static:Version", "2.0.0"));
        var analyzer = factory.Services.GetRequiredService<CommentAnalyzer>();

        var result = await analyzer.AnalyzeAsync("any comment", CancellationToken.None);

        Assert.Equal(Decision.Reject, result.Decision);
        Assert.Equal(0.95, result.Confidence);
        Assert.Equal("static@2.0.0", result.Model.Version);
    }

    [Theory]
    [InlineData("DecisionPolicy:ApproveThreshold", "0.3", "DecisionPolicy:ApproveThreshold")]
    [InlineData("Model:Provider", "", "Model:Provider is required")]
    [InlineData("Model:Static:Confidence", "1.5", "Model:Static:Confidence")]
    public async Task Startup_Fails_WhenConfigurationIsInvalid(string key, string value, string expectedMessage)
    {
        await using var factory = CreateFactory((key, value));

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
}
