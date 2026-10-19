using CommentAnalysis.Infrastructure.Models;
using CommentAnalysis.Infrastructure.Models.SystemOne;
using CommentAnalysis.Infrastructure.Options;

namespace CommentAnalysis.IntegrationTests.Models;

public class ModelOptionsValidatorTests
{
    [Fact]
    public void ValidSystemOneOptions_Pass() => Assert.True(Validate(_ => { }).Succeeded);

    [Theory]
    [InlineData("http://localhost:1234/v1/")]
    [InlineData("http://127.0.0.1:1234/v1/")]
    [InlineData("http://host.docker.internal:1234/v1/")]
    [InlineData("https://api.typesafe.ai/v1/")]
    public void ApiKey_IsAllowed_OverHttpsOrLocally(string baseUrl) =>
        Assert.True(Validate(options => (options.BaseUrl, options.ApiKey) = (new Uri(baseUrl), "secret")).Succeeded);

    [Fact]
    public void ApiKey_OverPlainHttpToARemoteHost_IsInvalid() =>
        AssertFails(options => (options.BaseUrl, options.ApiKey) = (new Uri("http://model.example.com/v1/"), "secret"), "must use https");

    [Fact]
    public void RemoteHttpWithoutApiKey_IsAllowed() =>
        Assert.True(Validate(options => options.BaseUrl = new Uri("http://model.internal:1234/v1/")).Succeeded);

    [Fact]
    public void NonHttpBaseUrl_IsInvalid() =>
        AssertFails(options => options.BaseUrl = new Uri("file:///etc/passwd"), "BaseUrl must be an absolute http(s) URL");

    [Fact]
    public void RelativeBaseUrl_IsInvalid() =>
        AssertFails(options => options.BaseUrl = new Uri("/v1/", UriKind.Relative), "BaseUrl must be an absolute http(s) URL");

    [Fact]
    public void TooLongModelAndVersion_IsInvalid() =>
        AssertFails(options => (options.Model, options.Version) = (new string('m', 100), new string('v', 87)), "together must be at most 186");

    [Fact]
    public void LongestModelAndVersion_IsValid() =>
        Assert.True(Validate(options => (options.Model, options.Version) = (new string('m', 100), new string('v', 86))).Succeeded);

    [Fact]
    public void DefaultTimeout_CoversASlowLocalModel() => Assert.Equal(TimeSpan.FromMinutes(5), new SystemOneModelOptions().Timeout);

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:10:01")]
    public void TimeoutOutsideRange_IsInvalid(string timeout) =>
        AssertFails(options => options.Timeout = TimeSpan.Parse(timeout, System.Globalization.CultureInfo.InvariantCulture), "Timeout must be within");

    private static void AssertFails(Action<SystemOneModelOptions> configure, string expected)
    {
        var result = Validate(configure);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains(expected, StringComparison.Ordinal));
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(Action<SystemOneModelOptions> configure)
    {
        var options = new ModelOptions
        {
            Provider = ModelProvider.SystemOne,
            SystemOne = new SystemOneModelOptions { BaseUrl = new Uri("http://localhost:1234/v1/"), Model = "basal-2.0-mini", Version = "2.0" },
        };
        configure(options.SystemOne);
        return new ModelOptionsValidator().Validate(null, options);
    }
}
