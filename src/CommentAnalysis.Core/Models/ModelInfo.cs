namespace CommentAnalysis.Core.Models;

/// <summary>
/// Identifies the model that produced a prediction. <see cref="Version"/> is the full,
/// persisted version string, so two predictions with the same version are comparable.
/// </summary>
public sealed record ModelInfo
{
    /// <summary>Maximum length of <see cref="Name"/> and <see cref="Version"/>; matches the database columns.</summary>
    public const int MaxLength = 200;

    public ModelInfo(string name, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, MaxLength, nameof(name));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(version.Length, MaxLength, nameof(version));

        Name = name;
        Version = version;
    }

    public string Name { get; }

    public string Version { get; }
}
