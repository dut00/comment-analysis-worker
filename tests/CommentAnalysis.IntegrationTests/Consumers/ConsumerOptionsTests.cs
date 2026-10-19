using System.Text;
using CommentAnalysis.Worker.Consumers;
using RabbitMQ.Client;

namespace CommentAnalysis.IntegrationTests.Consumers;

public class ConsumerOptionsTests
{
    [Fact]
    public void Defaults_AreValid() => Assert.Empty(new ConsumerOptions().GetValidationErrors());

    [Theory]
    [InlineData(1023)]
    [InlineData(16 * 1024 * 1024 + 1)]
    public void MaxMessageBytes_OutsideRange_IsInvalid(int maxMessageBytes)
    {
        var options = new ConsumerOptions { MaxMessageBytes = maxMessageBytes, MaxContentLength = 100 };

        Assert.Contains(options.GetValidationErrors(), error => error.Contains("MaxMessageBytes must", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64 * 1024 + 1)]
    public void MaxContentLength_OutsideRange_IsInvalid(int maxContentLength)
    {
        var options = new ConsumerOptions { MaxContentLength = maxContentLength };

        Assert.Contains(options.GetValidationErrors(), error => error.Contains("MaxContentLength", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void MaxRetries_OutsideRange_IsInvalid(int maxRetries)
    {
        var options = new ConsumerOptions { MaxRetries = maxRetries };

        Assert.Contains(options.GetValidationErrors(), error => error.Contains("MaxRetries", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("-00:00:01")]
    [InlineData("00:00:31")]
    public void RetryDelay_OutsideRange_IsInvalid(string retryDelay)
    {
        var options = new ConsumerOptions { RetryDelay = TimeSpan.Parse(retryDelay, System.Globalization.CultureInfo.InvariantCulture) };

        Assert.Contains(options.GetValidationErrors(), error => error.Contains("RetryDelay", StringComparison.Ordinal));
    }

    [Fact]
    public void GetRetryCount_IsZero_WithoutHeaders() =>
        Assert.Equal(0, CommentCreatedConsumer.GetRetryCount(new BasicProperties()));

    [Theory]
    [InlineData(3, 3)]
    [InlineData(3L, 3)]
    [InlineData(-5, 0)]
    [InlineData(long.MaxValue, int.MaxValue)]
    [InlineData("not a number", 0)]
    public void GetRetryCount_ReadsTheHeader(object value, int expected)
    {
        var header = value is string text ? Encoding.UTF8.GetBytes(text) : value;
        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?> { [CommentCreatedConsumer.RetryCountHeader] = header },
        };

        Assert.Equal(expected, CommentCreatedConsumer.GetRetryCount(properties));
    }

    [Fact]
    public void GetRetryCount_ParsesNumericText() =>
        Assert.Equal(7, CommentCreatedConsumer.GetRetryCount(new BasicProperties
        {
            Headers = new Dictionary<string, object?> { [CommentCreatedConsumer.RetryCountHeader] = "7"u8.ToArray() },
        }));

    [Fact]
    public void CreateRetryHeaders_KeepsApplicationHeaders_DropsRoutingAndBrokerHeaders_AndSetsTheCount()
    {
        var original = new Dictionary<string, object?>
        {
            ["traceparent"] = "00-abc-def-01"u8.ToArray(),
            ["CC"] = new List<object> { "other-service-queue"u8.ToArray() },
            ["BCC"] = new List<object> { "another-queue"u8.ToArray() },
            ["x-death"] = new List<object>(),
            ["x-first-death-queue"] = "q"u8.ToArray(),
            ["x-last-death-reason"] = "rejected"u8.ToArray(),
            ["x-delivery-count"] = 4L,
            [CommentCreatedConsumer.RetryCountHeader] = 1,
        };

        var headers = CommentCreatedConsumer.CreateRetryHeaders(original, retryCount: 2);

        Assert.Equal(["traceparent", CommentCreatedConsumer.RetryCountHeader], headers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, headers[CommentCreatedConsumer.RetryCountHeader]);
    }

    [Fact]
    public void CreateRetryHeaders_WithoutOriginalHeaders_SetsOnlyTheCount() =>
        Assert.Equal(1, Assert.Single(CommentCreatedConsumer.CreateRetryHeaders(null, retryCount: 1)).Value);

    [Fact]
    public void SanitizeForLog_ReplacesControlCharacters_AndTruncates()
    {
        var forged = "id\r\n{\"Level\":\"Critical\"}" + new string('x', 200);

        var sanitized = CommentCreatedConsumer.SanitizeForLog(forged);

        Assert.NotNull(sanitized);
        Assert.Equal(128, sanitized.Length);
        Assert.StartsWith("id??{", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(sanitized, char.IsControl);
    }

    [Fact]
    public void SanitizeForLog_KeepsNull() => Assert.Null(CommentCreatedConsumer.SanitizeForLog(null));
}
