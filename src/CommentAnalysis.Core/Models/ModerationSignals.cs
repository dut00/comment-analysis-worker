namespace CommentAnalysis.Core.Models;

/// <summary>
/// Secondary assessments of a comment, besides its label. They can only keep a comment from being approved
/// automatically (see <see cref="Decisions.DecisionPolicy"/>); they never approve or reject it on their own.
/// </summary>
public sealed record ModerationSignals
{
    public ModerationSignals(double toxicity, double spam, double personalData)
    {
        Toxicity = Validate(toxicity, nameof(toxicity));
        Spam = Validate(spam, nameof(spam));
        PersonalData = Validate(personalData, nameof(personalData));
    }

    /// <summary>How offensive the comment is, from 0 (not at all) to 1 (the most severe level).</summary>
    public double Toxicity { get; }

    /// <summary>The probability that the comment is spam (advertising, unrelated links, repeated text).</summary>
    public double Spam { get; }

    /// <summary>The probability that the comment reveals personal data (e.g. a phone number or an address).</summary>
    public double PersonalData { get; }

    private static double Validate(double value, string name) =>
        double.IsFinite(value) && value is >= 0 and <= 1
            ? value
            : throw new ArgumentOutOfRangeException(name, value, "A signal must be within [0, 1].");
}
