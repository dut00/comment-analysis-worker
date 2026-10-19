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
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence must be within [0, 1].");
        }

        Label = label;
        Confidence = confidence;
        Probabilities = probabilities ?? new Dictionary<string, double>();
    }

    /// <summary>The label the model chose.</summary>
    public ContentLabel Label { get; }

    /// <summary>The model's confidence in <see cref="Label"/>, within [0, 1].</summary>
    public double Confidence { get; }

    /// <summary>Model-specific probabilities per raw label, kept for diagnostics.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; }
}
