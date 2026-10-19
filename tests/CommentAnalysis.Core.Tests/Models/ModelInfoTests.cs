using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Core.Tests.Models;

public class ModelInfoTests
{
    [Theory]
    [InlineData("", "1.0.0")]
    [InlineData(" ", "1.0.0")]
    [InlineData("toxicity", "")]
    [InlineData("toxicity", " ")]
    public void Constructor_Throws_WhenNameOrVersionIsBlank(string name, string version)
    {
        Assert.Throws<ArgumentException>(() => new ModelInfo(name, version));
    }

    [Fact]
    public void Constructor_AcceptsMaxLength()
    {
        var value = new string('a', ModelInfo.MaxLength);

        var info = new ModelInfo(value, value);

        Assert.Equal(ModelInfo.MaxLength, info.Version.Length);
    }

    [Theory]
    [InlineData(ModelInfo.MaxLength + 1, 10)]
    [InlineData(10, ModelInfo.MaxLength + 1)]
    public void Constructor_Throws_WhenNameOrVersionIsTooLong(int nameLength, int versionLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ModelInfo(new string('n', nameLength), new string('v', versionLength)));
    }

    [Fact]
    public void Equality_IsBasedOnNameAndVersion()
    {
        Assert.Equal(new ModelInfo("jev", "jev-1.13.0"), new ModelInfo("jev", "jev-1.13.0"));
        Assert.NotEqual(new ModelInfo("jev", "jev-1.13.0"), new ModelInfo("jev", "jev-1.14.0"));
    }
}
