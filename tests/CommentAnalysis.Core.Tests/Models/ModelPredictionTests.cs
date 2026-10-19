using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Tests.Models;

public class ModelPredictionTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Constructor_AcceptsConfidence_WithinRange(double confidence)
    {
        var prediction = new ModelPrediction(ContentLabel.Acceptable, confidence);

        Assert.Equal(confidence, prediction.Confidence);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_Throws_WhenConfidenceIsOutOfRange(double confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelPrediction(ContentLabel.Acceptable, confidence));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_Throws_WhenAnyProbabilityIsOutOfRange(double probability)
    {
        var probabilities = new Dictionary<string, double> { ["toxic"] = 0.4, ["non-toxic"] = probability };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ModelPrediction(ContentLabel.Acceptable, 0.6, probabilities));

        Assert.Contains("non-toxic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Probabilities_AreCopied_SoLaterChangesDoNotAffectPrediction()
    {
        var probabilities = new Dictionary<string, double> { ["toxic"] = 0.9 };
        var prediction = new ModelPrediction(ContentLabel.Unacceptable, 0.9, probabilities);

        probabilities["toxic"] = 0.1;
        probabilities["spam"] = 0.5;

        Assert.Equal(0.9, prediction.Probabilities["toxic"]);
        Assert.False(prediction.Probabilities.ContainsKey("spam"));
    }

    [Fact]
    public void Probabilities_DefaultsToEmpty()
    {
        var prediction = new ModelPrediction(ContentLabel.Unacceptable, 0.9);

        Assert.Empty(prediction.Probabilities);
    }
}
