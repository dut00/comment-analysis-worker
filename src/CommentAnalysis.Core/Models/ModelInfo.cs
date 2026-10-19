namespace CommentAnalysis.Core.Models;

/// <summary>
/// Identifies the model that produced a prediction. <see cref="Version"/> is the full,
/// persisted version string, so two predictions with the same version are comparable.
/// </summary>
public sealed record ModelInfo
{
    public ModelInfo(string name, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        Name = name;
        Version = version;
    }

    public string Name { get; }

    public string Version { get; }
}
