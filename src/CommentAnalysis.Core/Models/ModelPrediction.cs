using System.Collections.ObjectModel;

namespace CommentAnalysis.Core.Models;

/// <summary>
/// The raw output of a decision model for a single comment.
/// </summary>
public sealed record ModelPrediction
{
    public ModelPrediction(
        ContentLabel label,
        double confidence,
        IReadOnlyDictionary<string, double>? probabilities = null)
    {
        if (!IsProbability(confidence))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence must be within [0, 1].");
        }

        if (probabilities?.FirstOrDefault(pair => !IsProbability(pair.Value)) is { Key: not null } invalid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(probabilities),
                invalid.Value,
                $"Probability for '{invalid.Key}' must be within [0, 1].");
        }

        Label = label;
        Confidence = confidence;

        // Copied so a model cannot change the prediction after it was made.
        Probabilities = probabilities is null
            ? ReadOnlyDictionary<string, double>.Empty
            : new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(probabilities));
    }

    /// <summary>The label the model chose.</summary>
    public ContentLabel Label { get; }

    /// <summary>The model's confidence in <see cref="Label"/>, within [0, 1].</summary>
    public double Confidence { get; }

    /// <summary>Model-specific probabilities per raw label, kept for diagnostics.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; }

    private static bool IsProbability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
