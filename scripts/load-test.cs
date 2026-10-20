#:project ../src/CommentAnalysis.Infrastructure/CommentAnalysis.Infrastructure.csproj
#:property PublishAot=false

// Load test against the compose stack (phase H2): publishes comments, then checks that each one was analyzed once, has
// at most one outbox message, and that its CommentAnalyzed event reached RabbitMQ – also after the workers were
// stopped, killed, restarted or scaled in between.
//
//   dotnet run scripts/load-test.cs -- publish <count> <run.json>     # creates the run's event queue, then publishes
//   dotnet run scripts/load-test.cs -- verify <run.json> [timeout-s]  # waits for the workers, then reports
//
// Meant for a fast model: the static model (docker-compose.static.yml) or Jev (docker-compose.jev.yml). The local
// Basal server takes about 11 s per comment. The model version must not change during a run (a new version analyzes
// the comments again, which verify reports as a failure).
//
// The contents cycle through tests/data/polish-comments.json. Addresses default to compose's (LOADTEST_DB,
// LOADTEST_AMQP override them). Each run collects its events in its own queue (load-test.<run id>, bound to
// comment.analyzed, expiring an hour after its last use); verify deletes it only when the run passed, so a timed-out or
// failed verify can be run again. Which replica analyzed a comment is not stored: count "Comment analyzed" in each
// worker's log for that.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommentAnalysis.Core.Messages;
using Npgsql;
using RabbitMQ.Client;

const string Exchange = "comments";
const string InputQueue = "comment-analysis.comment-created";
const string DeadLetterQueue = "comment-analysis.comment-created.dlq";

var database = Environment.GetEnvironmentVariable("LOADTEST_DB")
    ?? "Host=localhost;Port=5433;Database=comment_analysis;Username=postgres;Password=postgres";
var broker = new Uri(Environment.GetEnvironmentVariable("LOADTEST_AMQP") ?? "amqp://guest:guest@localhost:5672/");

return args switch
{
    ["publish", var count, var runPath] when int.TryParse(count, CultureInfo.InvariantCulture, out var n) && n > 0 =>
        await PublishAsync(n, runPath),
    ["verify", var runPath] => await VerifyAsync(runPath, TimeSpan.FromMinutes(5)),
    ["verify", var runPath, var timeout] when int.TryParse(timeout, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 =>
        await VerifyAsync(runPath, TimeSpan.FromSeconds(seconds)),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: load-test.cs publish <count> <run.json> | verify <run.json> [timeout-seconds]");
    return 1;
}

static string EventQueue(Run run) => $"load-test.{run.Id:N}";

async Task<int> PublishAsync(int count, string runPath)
{
    var contents = JsonDocument.Parse(File.ReadAllText("tests/data/polish-comments.json")).RootElement
        .GetProperty("comments").EnumerateArray().Select(comment => comment.GetProperty("content").GetString()!).ToList();
    var run = new Run(Guid.NewGuid(), []);

    await using var connection = await new ConnectionFactory { Uri = broker }.CreateConnectionAsync();
    await using var channel = await connection.CreateChannelAsync(
        new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));

    // Declared before publishing, so no event is missed; it expires if verify never runs.
    await channel.QueueDeclareAsync(
        EventQueue(run),
        durable: true,
        exclusive: false,
        autoDelete: false,
        arguments: new Dictionary<string, object?> { ["x-expires"] = (int)TimeSpan.FromHours(1).TotalMilliseconds });
    await channel.QueueBindAsync(EventQueue(run), Exchange, "comment.analyzed");

    var stopwatch = Stopwatch.StartNew();
    try
    {
        for (var i = 0; i < count; i++)
        {
            var comment = new CommentCreated
            {
                CommentId = Guid.NewGuid(),
                PostId = Guid.NewGuid(),
                AuthorId = Guid.NewGuid(),
                Content = contents[i % contents.Count],
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await channel.BasicPublishAsync(
                Exchange,
                "comment.created",
                mandatory: true,
                new BasicProperties { ContentType = "application/json", Persistent = true, MessageId = comment.CommentId.ToString() },
                MessageSerializer.Serialize(comment));
            run.Ids.Add(comment.CommentId);
        }
    }
    finally
    {
        // Also after an interruption: the comments already published can still be verified.
        File.WriteAllText(runPath, JsonSerializer.Serialize(run));
    }

    Console.WriteLine($"Published {count} comments in {stopwatch.Elapsed.TotalSeconds:F1} s; run in {runPath}");
    return 0;
}

async Task<int> VerifyAsync(string runPath, TimeSpan timeout)
{
    var run = JsonSerializer.Deserialize<Run>(File.ReadAllText(runPath))!;
    var stopwatch = Stopwatch.StartNew();

    await using var db = NpgsqlDataSource.Create(database);
    Counts counts;
    while (true)
    {
        counts = await CountAsync(db, run.Ids);
        if (counts.Analyzed == run.Ids.Count && counts.Unpublished == 0)
        {
            break;
        }

        if (stopwatch.Elapsed > timeout)
        {
            Console.WriteLine($"Timed out: analyzed {counts.Analyzed} of {run.Ids.Count}, outbox unpublished {counts.Unpublished}. The events are kept; run verify again.");
            return 3;
        }

        Console.WriteLine($"  {stopwatch.Elapsed.TotalSeconds:F0} s: analyzed {counts.Analyzed} of {run.Ids.Count}, outbox unpublished {counts.Unpublished}");
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    // Outbox rows are marked published only after the broker confirmed them, so their events are queued by now.
    await using var connection = await new ConnectionFactory { Uri = broker }.CreateConnectionAsync();
    await using var channel = await connection.CreateChannelAsync();
    var events = new List<Event>();
    while (await channel.BasicGetAsync(EventQueue(run), autoAck: false) is { } delivery)
    {
        // Not acked: unless the run passes, closing the channel returns them for another verify.
        if (delivery.BasicProperties.Type == CommentAnalyzed.MessageType)
        {
            var analyzed = MessageSerializer.Deserialize<CommentAnalyzed>(delivery.Body.Span);
            events.Add(new Event(analyzed.CommentId, delivery.BasicProperties.MessageId ?? string.Empty));
        }
    }

    var ids = run.Ids.ToHashSet();
    events = events.Where(e => ids.Contains(e.CommentId)).ToList();
    var input = await channel.QueueDeclarePassiveAsync(InputQueue);
    var deadLettered = await channel.QueueDeclarePassiveAsync(DeadLetterQueue);

    var redelivered = events.Count - events.Select(e => e.MessageId).Distinct().Count();
    var eventComments = events.Select(e => e.CommentId).ToHashSet();
    var commentsWithSeveralRows = events.GroupBy(e => e.CommentId).Count(group => group.Select(e => e.MessageId).Distinct().Count() > 1);

    Console.WriteLine();
    Console.WriteLine($"Comments:                               {run.Ids.Count}");
    Console.WriteLine($"Analyzed (distinct comments):           {counts.Analyzed}");
    Console.WriteLine($"Comments with more than one analysis:   {counts.SeveralAnalyses}");
    Console.WriteLine($"Comments with more than one outbox row: {counts.SeveralOutboxRows}");
    Console.WriteLine($"Outbox rows not published:              {counts.Unpublished}");
    Console.WriteLine($"Events received:                        {events.Count} ({redelivered} redelivered copies of one outbox row)");
    Console.WriteLine($"Comments without an event:              {run.Ids.Count - eventComments.Count}");
    Console.WriteLine($"Comments with events of several rows:   {commentsWithSeveralRows}");
    Console.WriteLine($"Input queue / DLQ messages now:         {input.MessageCount} / {deadLettered.MessageCount}");
    Console.WriteLine($"Waited:                                 {stopwatch.Elapsed.TotalSeconds:F0} s");

    // Redelivered copies of the same outbox row are allowed (at-least-once; consumers deduplicate on the message id).
    // More than one analysis per comment would also need another model version: the unique key forbids it otherwise.
    var passed = counts.Analyzed == run.Ids.Count
        && counts.SeveralAnalyses == 0
        && counts.SeveralOutboxRows == 0
        && counts.Unpublished == 0
        && eventComments.Count == run.Ids.Count
        && commentsWithSeveralRows == 0;
    if (passed)
    {
        await channel.QueueDeleteAsync(EventQueue(run));
    }

    Console.WriteLine(passed ? "PASSED" : "FAILED (the events are kept; fix and run verify again)");
    return passed ? 0 : 2;
}

static async Task<Counts> CountAsync(NpgsqlDataSource db, List<Guid> ids)
{
    await using var command = db.CreateCommand("""
        SELECT
          (SELECT count(DISTINCT comment_id) FROM comment_analyses WHERE comment_id = ANY($1)),
          (SELECT count(*) FROM (SELECT comment_id FROM comment_analyses WHERE comment_id = ANY($1) GROUP BY comment_id HAVING count(*) > 1) AS several),
          (SELECT count(*) FROM (SELECT payload->>'commentId' FROM outbox_messages WHERE type = 'CommentAnalyzed' AND (payload->>'commentId')::uuid = ANY($1) GROUP BY 1 HAVING count(*) > 1) AS several),
          (SELECT count(*) FROM outbox_messages WHERE type = 'CommentAnalyzed' AND (payload->>'commentId')::uuid = ANY($1) AND published_at IS NULL)
        """);
    command.Parameters.Add(new NpgsqlParameter { Value = ids.ToArray() });
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new Counts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
}

internal sealed record Run(Guid Id, List<Guid> Ids);

internal sealed record Counts(long Analyzed, long SeveralAnalyses, long SeveralOutboxRows, long Unpublished);

internal sealed record Event(Guid CommentId, string MessageId);
