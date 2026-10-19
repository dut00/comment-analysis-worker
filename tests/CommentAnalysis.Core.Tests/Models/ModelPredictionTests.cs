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

    [Fact]
    public void Probabilities_DefaultsToEmpty()
    {
        var prediction = new ModelPrediction(ContentLabel.Unacceptable, 0.9);

        Assert.Empty(prediction.Probabilities);
    }
}
