#:project ../src/CommentAnalysis.Infrastructure/CommentAnalysis.Infrastructure.csproj
#:property PublishAot=false

// Quality check of the /v1/systemone decision model on the labelled comments in tests/data.
//
//   dotnet run scripts/quality-report.cs -- <comments.json> <results.json> <report.md> [base-url] [model] [version]
//
// Evaluates every comment without a successful result in <results.json> (so an interrupted run resumes and failures are
// retried), saves each prediction
// as soon as it arrives, then writes a Markdown report: the decisions at the default policy, label accuracy,
// latency, and how the agreement changes with the thresholds. With every comment already in <results.json> it only
// recomputes the report, so thresholds can be explored without querying the model again.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommentAnalysis.Core.Decisions;
using CommentAnalysis.Core.Models;
using CommentAnalysis.Infrastructure.Models.SystemOne;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: quality-report.cs <comments.json> <results.json> <report.md> [base-url] [model] [version]");
    return 1;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var samples = JsonSerializer.Deserialize<SampleFile>(File.ReadAllText(args[0]), json)!.Comments;
var resultsPath = args[1];
var model = new SystemOneDecisionModel(new LongTimeoutFactory(), new SystemOneModelOptions
{
    BaseUrl = new Uri(args.Length > 3 ? args[3] : "http://localhost:1234/v1/"),
    Model = args.Length > 4 ? args[4] : "basal-2.0-mini",
    Version = args.Length > 5 ? args[5] : "2.0",
});

// Results of another model version (e.g. after the questions changed) are evaluated again.
var results = File.Exists(resultsPath)
    ? JsonSerializer.Deserialize<List<Result>>(File.ReadAllText(resultsPath), json)!
        .Where(result => result.Error is null && result.ModelVersion == model.Info.Version)
        .ToList()
    : [];

foreach (var sample in samples.Where(sample => results.All(result => result.Id != sample.Id)))
{
    var stopwatch = Stopwatch.StartNew();
    Result result;
    try
    {
        var prediction = await model.EvaluateAsync(sample.Content, CancellationToken.None);
        result = new Result(
            sample.Id,
            model.Info.Version,
            prediction.Label,
            prediction.Confidence,
            prediction.Signals!.Toxicity,
            prediction.Signals.Spam,
            prediction.Signals.PersonalData,
            stopwatch.Elapsed.TotalSeconds,
            null);
    }
    catch (ModelUnavailableException exception)
    {
        // Every other comment would fail the same way; the saved results stay, so a later run resumes.
        Console.Error.WriteLine($"The model is unavailable ({exception.Message}); stopping.");
        return 2;
    }
    catch (Exception exception)
    {
        result = new Result(sample.Id, model.Info.Version, null, null, null, null, null, stopwatch.Elapsed.TotalSeconds, exception.GetType().Name);
    }

    results.Add(result);
    File.WriteAllText(resultsPath, JsonSerializer.Serialize(results, json));
    Console.WriteLine(FormattableString.Invariant($"{sample.Id}: {result.Label} {result.Confidence:F3} in {result.Seconds:F0} s {result.Error}"));
}

File.WriteAllText(args[2], Report(samples, results));
Console.WriteLine($"Report: {args[2]}");
return 0;

static string Report(IReadOnlyList<Sample> samples, IReadOnlyList<Result> results)
{
    var defaults = new DecisionPolicyOptions();
    var rows = samples
        .Select(sample => (Sample: sample, Result: results.Single(result => result.Id == sample.Id)))
        .ToList();
    var evaluated = rows.Where(row => row.Result.Error is null).ToList();
    var report = new StringBuilder();

    report.AppendLine(CultureInfo.InvariantCulture, $"# Decision model quality report");
    report.AppendLine();
    report.AppendLine(CultureInfo.InvariantCulture, $"Model version: `{string.Join("`, `", results.Select(result => result.ModelVersion).Distinct())}`. Comments: {rows.Count}, evaluated: {evaluated.Count}, failed: {rows.Count - evaluated.Count}.");
    report.AppendLine(CultureInfo.InvariantCulture, $"Default policy: approve ≥ {defaults.ApproveThreshold}, reject ≥ {defaults.RejectThreshold}, toxicity ≤ {defaults.MaxToxicityForApprove}, spam ≤ {defaults.MaxSpamForApprove}, personal data ≤ {defaults.MaxPersonalDataForApprove}.");
    report.AppendLine();

    var decided = evaluated.Select(row => (row.Sample, row.Result, Decision: Decide(row.Result, defaults))).ToList();
    report.AppendLine("## Summary at the default policy");
    report.AppendLine();
    report.AppendLine(CultureInfo.InvariantCulture, $"- Decision as expected: {Count(decided, row => Matches(row.Sample.Expected, row.Decision.Decision))}");
    var labelled = decided.Where(row => row.Sample.Expected is "approve" or "reject").ToList();
    report.AppendLine(CultureInfo.InvariantCulture, $"- Verdict label right (expected approve/reject, any confidence): {Count(labelled, row => row.Result.Label == (row.Sample.Expected == "approve" ? ContentLabel.Acceptable : ContentLabel.Unacceptable))}");
    report.AppendLine(CultureInfo.InvariantCulture, $"- **Wrongly rejected** (expected approve or review, got Reject): {Count(decided, row => row.Sample.Expected != "reject" && row.Decision.Decision == Decision.Reject)}");
    report.AppendLine(CultureInfo.InvariantCulture, $"- **Wrongly approved** (expected reject or review, got Approve): {Count(decided, row => row.Sample.Expected != "approve" && row.Decision.Decision == Decision.Approve)}");
    report.AppendLine(CultureInfo.InvariantCulture, $"- Sent to manual review: {Count(decided, row => row.Decision.Decision == Decision.ManualReview)}");
    if (evaluated.Count > 0)
    {
        var seconds = evaluated.Select(row => row.Result.Seconds).Order().ToList();
        report.AppendLine(CultureInfo.InvariantCulture, $"- Latency: median {seconds[seconds.Count / 2]:F0} s, max {seconds[^1]:F0} s, total {seconds.Sum() / 60:F0} min.");
    }

    report.AppendLine();
    report.AppendLine("Expected (rows) against decided (columns):");
    report.AppendLine();
    report.AppendLine("| expected | Approve | Reject | ManualReview |");
    report.AppendLine("|---|---|---|---|");
    foreach (var expected in new[] { "approve", "reject", "review" })
    {
        var group = decided.Where(row => row.Sample.Expected == expected).ToList();
        report.AppendLine(CultureInfo.InvariantCulture, $"| {expected} | {group.Count(row => row.Decision.Decision == Decision.Approve)} | {group.Count(row => row.Decision.Decision == Decision.Reject)} | {group.Count(row => row.Decision.Decision == Decision.ManualReview)} |");
    }

    report.AppendLine();
    report.AppendLine("## Comments");
    report.AppendLine();
    report.AppendLine("| id | category | expected | decision | reasons | label | confidence | toxicity | spam | personal data | seconds |");
    report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
    foreach (var (sample, result) in rows)
    {
        if (result.Error is not null)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"| {sample.Id} | {sample.Category} | {sample.Expected} | **failed: {result.Error}** | | | | | | | {result.Seconds:F0} |");
            continue;
        }

        var decision = Decide(result, defaults);
        var mark = Matches(sample.Expected, decision.Decision) ? string.Empty : " ✗";
        report.AppendLine(CultureInfo.InvariantCulture, $"| {sample.Id} | {sample.Category} | {sample.Expected} | {decision.Decision}{mark} | {string.Join(", ", decision.ReviewReasons)} | {result.Label} | {result.Confidence:F3} | {result.Toxicity:F2} | {result.Spam:F2} | {result.PersonalData:F2} | {result.Seconds:F0} |");
    }

    report.AppendLine();
    report.AppendLine("## Thresholds");
    report.AppendLine();
    report.AppendLine("Decisions as expected / wrongly rejected / wrongly approved / manual reviews, for other approve thresholds (rows) and toxicity limits (columns); the other settings stay at their defaults.");
    report.AppendLine();
    double[] toxicityLimits = [0.30, 0.35, 0.40, 0.45, 0.50];
    report.AppendLine(CultureInfo.InvariantCulture, $"| approve ≥ | {string.Join(" | ", toxicityLimits.Select(limit => FormattableString.Invariant($"toxicity ≤ {limit:F2}")))} |");
    report.AppendLine(CultureInfo.InvariantCulture, $"|---|{string.Concat(toxicityLimits.Select(_ => "---|"))}");
    foreach (var approve in new[] { 0.75, 0.80, 0.85, 0.90 })
    {
        var cells = toxicityLimits.Select(limit => Outcome(evaluated, new DecisionPolicyOptions { ApproveThreshold = approve, MaxToxicityForApprove = limit }));
        report.AppendLine(CultureInfo.InvariantCulture, $"| {approve:F2} | {string.Join(" | ", cells)} |");
    }

    report.AppendLine();
    report.AppendLine("| reject ≥ | outcome |");
    report.AppendLine("|---|---|");
    foreach (var reject in new[] { 0.80, 0.85, 0.90, 0.95 })
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"| {reject:F2} | {Outcome(evaluated, new DecisionPolicyOptions { RejectThreshold = reject })} |");
    }

    return report.ToString();
}

static string Outcome(IReadOnlyList<(Sample Sample, Result Result)> rows, DecisionPolicyOptions options)
{
    var decided = rows.Select(row => (row.Sample, Decision: Decide(row.Result, options).Decision)).ToList();
    return string.Create(
        CultureInfo.InvariantCulture,
        $"{decided.Count(row => Matches(row.Sample.Expected, row.Decision))} / {decided.Count(row => row.Sample.Expected != "reject" && row.Decision == Decision.Reject)} / {decided.Count(row => row.Sample.Expected != "approve" && row.Decision == Decision.Approve)} / {decided.Count(row => row.Decision == Decision.ManualReview)}");
}

static PolicyDecision Decide(Result result, DecisionPolicyOptions options) =>
    new DecisionPolicy(options).Decide(new ModelPrediction(
        result.Label!.Value,
        result.Confidence!.Value,
        signals: new ModerationSignals(result.Toxicity!.Value, result.Spam!.Value, result.PersonalData!.Value)));

static bool Matches(string expected, Decision decision) => expected switch
{
    "approve" => decision == Decision.Approve,
    "reject" => decision == Decision.Reject,
    _ => decision == Decision.ManualReview,
};

static string Count<T>(IReadOnlyCollection<T> rows, Func<T, bool> predicate) =>
    string.Create(CultureInfo.InvariantCulture, $"{rows.Count(predicate)} of {rows.Count}");

internal sealed record SampleFile(IReadOnlyList<Sample> Comments);

internal sealed record Sample(string Id, string Category, string Expected, string Content);

internal sealed record Result(
    string Id,
    string ModelVersion,
    ContentLabel? Label,
    double? Confidence,
    double? Toxicity,
    double? Spam,
    double? PersonalData,
    double Seconds,
    string? Error);

/// <summary>Long comments can take several minutes on a CPU; the report wants an answer, not a timeout.</summary>
internal sealed class LongTimeoutFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromMinutes(15) };
}
