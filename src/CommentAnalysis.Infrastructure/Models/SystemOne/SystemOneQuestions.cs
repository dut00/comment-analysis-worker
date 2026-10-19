using System.Text.Json.Nodes;

namespace CommentAnalysis.Infrastructure.Models.SystemOne;

/// <summary>
/// The questions asked about every comment. They are part of the model version, so editing any text here starts a
/// new version and never mixes analyses of different questions.
/// </summary>
internal static class SystemOneQuestions
{
    public const string Verdict = "verdict";
    public const string Toxicity = "toxicity";
    public const string Spam = "spam";
    public const string PersonalData = "personal_data";

    public const string Approve = "approve";
    public const string Reject = "reject";

    /// <summary>The highest level of the toxicity score (four levels, 0–3).</summary>
    public const int MaxToxicityLevel = 3;

    /// <summary>The <c>questions</c> object of the request, serialized once.</summary>
    public static readonly string Json = new JsonObject
    {
        [Verdict] = new JsonObject
        {
            ["type"] = "choice",
            ["instructions"] = "Should this comment be published under an article on a Polish-speaking website?",
            ["criteria"] = new JsonObject
            {
                [Approve] = "Respectful and relevant. Criticism, disagreement and strong opinions are allowed.",
                [Reject] = "Insults, hate speech, threats, harassment, obscene content or spam.",
            },
        },
        [Toxicity] = new JsonObject
        {
            ["type"] = "score",
            ["instructions"] = "How offensive are the comment's language and intent?",
            ["criteria"] = new JsonArray(
                "none",
                "mild: rude, sarcastic or mild swearing",
                "offensive: insults or vulgarity aimed at people",
                "severe: hate speech or threats"),
        },
        [Spam] = new JsonObject
        {
            ["type"] = "noul",
            ["instructions"] = "Is the comment spam: advertising, links to unrelated offers, or repeated text?",
            ["criteria"] = new JsonObject
            {
                ["true"] = "The comment is spam.",
                ["false"] = "The comment is not spam.",
            },
        },
        [PersonalData] = new JsonObject
        {
            ["type"] = "noul",
            ["instructions"] = "Does the comment reveal personal data, such as a phone number, an address, an e-mail address or an ID number?",
            ["criteria"] = new JsonObject
            {
                ["true"] = "The comment reveals personal data.",
                ["false"] = "The comment does not reveal personal data.",
            },
        },
    }.ToJsonString();
}
