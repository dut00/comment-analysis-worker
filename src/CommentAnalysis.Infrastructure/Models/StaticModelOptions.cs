using CommentAnalysis.Core.Models;

namespace CommentAnalysis.Infrastructure.Models;

public sealed class StaticModelOptions
{
    public string Name { get; set; } = "static";

    public string Version { get; set; } = "1.0.0";

    public ContentLabel Label { get; set; } = ContentLabel.Acceptable;

    public double Confidence { get; set; } = 0.99;
}
