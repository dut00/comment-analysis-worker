# Comment Analysis Worker

A .NET 10 worker that moderates newly created comments. It consumes `CommentCreated` from RabbitMQ, asks a decision model whether the comment may be published, and decides **Approve**, **Reject** or **ManualReview**. It stores the decision, the model's confidence and the exact model version in PostgreSQL and publishes `CommentAnalyzed` through a transactional outbox. It is stateless and runs in containers, as one replica or several.

The decision model is queried through the TypeSafe-compatible `POST /v1/systemone` API: TypeSafe's Jev in the cloud (through OpenRouter) or Basal 2.0 mini on a local server. The design and the history of every decision are in [docs/implementation-plan.md](docs/implementation-plan.md); production hardening (phase H4) is still open.

- [How it works](#how-it-works)
- [Quick start](#quick-start)
- [Running without containers](#running-without-containers)
- [Decision models](#decision-models)
- [Messages](#messages)
- [Configuration](#configuration)
- [Health and metrics](#health-and-metrics)
- [Scaling and load test](#scaling-and-load-test)
- [Deploying to production](#deploying-to-production)
- [Development](#development)

## How it works

```
comment service ──CommentCreated──▶ exchange "comments" (topic, routing key comment.created)
                                       │
                                       ▼
                     queue comment-analysis.comment-created (quorum) ──dead letters──▶ …comment-created.dlq
                                       │
                                       ▼
   ┌────────────────────────── worker (one or more replicas) ─────────────────────────────┐
   │ CommentCreatedConsumer                                                               │
   │   1. skip if this comment was already analyzed by the current model version          │
   │   2. ask the decision model four questions (POST /v1/systemone)                      │
   │   3. DecisionPolicy → Approve / Reject / ManualReview (+ review reasons)             │
   │   4. one DB transaction: comment_analyses row + outbox_messages row                  │
   │   5. ack the message                                                                 │
   │ OutboxPublisher: publishes outbox rows with publisher confirms, then marks them      │
   └──────────────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
                     exchange "comments", routing key comment.analyzed ──CommentAnalyzed──▶ subscribers
```

**The model's answers.** One request asks four questions about the comment, and the model answers from its output distribution rather than by generating text:

| question | type | used as |
|---|---|---|
| `verdict` – should the comment be published? | choice `approve` / `reject` | the label; the probability of the chosen option is the **confidence** |
| `toxicity` | score 0–3 (none, mild, offensive, severe) | a signal (÷ 3) |
| `spam` | yes/no | a signal (probability of yes) |
| `personal_data` | yes/no | a signal (probability of yes) |

**The decision policy** (`DecisionPolicy`, thresholds in `DecisionPolicy:*`):

- **Reject** – the verdict is `reject` with confidence ≥ 0.85.
- **Approve** – the verdict is `approve` with confidence ≥ 0.85 and no signal above its limit (toxicity 0.35, spam 0.5, personal data 0.5).
- **ManualReview** – everything else, with the reasons stored next to the decision: `LowConfidence`, `Toxicity`, `Spam`, `PersonalData`, or `TooLong` for a comment over `Consumer:MaxContentLength` (1000 characters), which is not sent to the model at all. Signals can only stop an approval; they never reject.

**Guarantees.**

- *Idempotent.* `comment_analyses` has a unique key on comment and model version, and the worker checks it before asking the model, so a redelivered message is acknowledged without a second analysis.
- *No lost events.* The analysis and its `CommentAnalyzed` are written in one transaction; the publisher marks an outbox row only after the broker confirmed the message. Events are delivered at least once – consumers deduplicate on the message id (the outbox row id).
- *Replicas.* Each outbox batch is claimed with `FOR UPDATE SKIP LOCKED`, so replicas publish different rows; any replica can process any comment.
- *Model version.* Every analysis records the model version: `{model}@{version}+{hash}`, where the hash covers the questions and how the comment is framed, so changing a question always makes a new version.

**Failures.**

- A malformed message, or one larger than `Consumer:MaxMessageBytes`, goes straight to the dead-letter queue.
- A processing error (e.g. an invalid model answer, the database down) is retried by republishing the message with an `x-retry-count` header – 1 s, 2 s, 3 s – and dead-lettered after `Consumer:MaxRetries` (3).
- An **unavailable model** never uses up retries: the message returns to the queue after a back-off (1 s doubling to 30 s, or the server's `Retry-After` if longer, up to 30 s) and readiness reports the model. That covers an unreachable server, another model answering, a rejected key or an empty account (401/402), a gateway without a working provider (502/503), a rate limit or an overload (429/529), and a timeout or another error status that the server's model list confirms.
- A worker crash leaves the message unacknowledged; RabbitMQ redelivers it and dead-letters it when it crashes a sixth time (the queue's delivery limit 5 lets a message be returned 5 times).
- On shutdown the worker stops consuming, lets in-flight messages finish for up to `Host:ShutdownTimeout` (20 s), and leaves the rest to be redelivered.

## Quick start

Requirements: Docker, and the .NET 10 SDK for the scripts and tests.

1. Start the stack with the static model, which approves everything at once and needs no model server:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.static.yml up --build -d
   ```

   Compose starts PostgreSQL (`localhost:5433`), RabbitMQ (`localhost:5672`, UI on http://localhost:15672, guest/guest) and the worker (http://localhost:7461), which applies the database migrations and declares its RabbitMQ topology. Wait until `curl http://localhost:7461/health/ready` returns `Healthy`.

   For a real model use Basal (`docker compose up --build -d`, with the Basal server running – see [Decision models](#decision-models)) or Jev (`docker compose -f docker-compose.yml -f docker-compose.jev.yml up --build -d`, with a key in `.env`).

2. Send a comment. Change the ids for every new comment: an already analyzed comment is skipped.

   bash (also Git Bash on Windows) – `rabbitmqadmin` in the RabbitMQ container reads the message from stdin:

   ```bash
   echo '{"commentId":"3f0c1d9e-6a43-4c1b-9a51-0d6c0f6b2a11","postId":"8e2b7c44-1f5d-4a8e-b0c3-5d9e6f7a8b90","authorId":"c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e5f","content":"Świetny artykuł, dziękuję!","createdAt":"2026-10-20T12:00:00Z"}' | docker compose exec -T rabbitmq rabbitmqadmin --non-interactive publish message -e comments -k comment.created -p '{"content_type":"application/json"}' -f -
   ```

   PowerShell (Windows PowerShell 5.1 would add a byte order mark when piping to a program, which the worker rejects as malformed, so this goes through RabbitMQ's HTTP API, with a new id each time):

   ```powershell
   $id = [guid]::NewGuid().ToString()
   $comment = @{ commentId = $id; postId = $id; authorId = $id; content = 'Świetny artykuł, dziękuję!'; createdAt = '2026-10-20T12:00:00Z' } | ConvertTo-Json -Compress
   $body = @{ properties = @{ content_type = 'application/json' }; routing_key = 'comment.created'; payload = $comment; payload_encoding = 'string' } | ConvertTo-Json -Compress
   $auth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('guest:guest'))
   Invoke-RestMethod -Method Post -Uri 'http://localhost:15672/api/exchanges/%2F/comments/publish' -Headers @{ Authorization = $auth } -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
   ```

   Or publish the JSON in the RabbitMQ UI: Exchanges → `comments` → Publish message, routing key `comment.created`, property `content_type=application/json`.

3. See the result (the second line in PowerShell: `docker compose logs worker | Select-String "Comment analyzed"`):

   ```bash
   docker compose exec postgres psql -U postgres -d comment_analysis -c "SELECT decision, review_reasons, confidence, model_version FROM comment_analyses ORDER BY analyzed_at DESC LIMIT 5;"
   docker compose logs worker | grep "Comment analyzed"
   ```

   The published `CommentAnalyzed` goes to the `comments` exchange with the routing key `comment.analyzed`; bind a queue to it in the RabbitMQ UI to see the events.

To reset everything: `docker compose down -v`.

## Running without containers

PostgreSQL and RabbitMQ still come from compose; the worker runs from the SDK on the same port (7461) as in the container, so stop the compose worker first (`docker compose stop worker`).

```bash
docker compose up -d postgres rabbitmq
dotnet run --project src/CommentAnalysis.Worker                          # Development settings: Basal on localhost:1234
dotnet run --project src/CommentAnalysis.Worker --launch-profile jev     # Jev through OpenRouter (key: see below)
```

`appsettings.Development.json` points at the compose services, applies migrations at startup and selects Basal; it is not published with the worker. To use the static model instead, set `Model__Provider=Static` in the environment.

## Decision models

| model | where it runs | accuracy on the labelled sample | time per comment | notes |
|---|---|---|---|---|
| Jev 1.14 (TypeSafe) | OpenRouter (cloud) | 30 of 32 as expected | ~0.4 s | ~$0.00002 per comment; comment content (personal data) goes to OpenRouter and TypeSafe, which needs a data-processing assessment before production use |
| Basal 2.0 mini | a local `basal-serve` | 28 of 32 as expected | ~11 s (CPU) | one request at a time |
| static | in the worker | – | instant | approves everything; for development and load tests |

Neither model approved a comment it should not have. The comment-by-comment comparison is in [docs/quality/comparison.md](docs/quality/comparison.md), the reports per model next to it.

### Basal on a local server

The configuration that works best on the author's laptop (CPU) runs the Q8_0 GGUF build with `basal-serve` (installation and the model files: [docs/basal-windows.md](docs/basal-windows.md)):

```powershell
basal-serve --model "C:\Users\Kuba\.lmstudio\models\Remek\basal-2.0-mini-GGUF" --mode gguf --gguf "C:\Users\Kuba\.lmstudio\models\Remek\basal-2.0-mini-GGUF\basal-2.0-mini-Q8_0.gguf" --name basal-2.0-mini --port 1234
```

- `--name basal-2.0-mini` matters: the server reports this name in every response, and the worker treats any other name as another model (the comments wait). Without it, `basal-serve` on Windows reports the whole `--model` path.
- The server listens on all interfaces (`0.0.0.0:1234`), so the worker in compose reaches it through `host.docker.internal`. It has no authentication: on a shared network, allow the port only from the Docker host (firewall).
- Check it with `curl http://localhost:1234/v1/models` (lists `basal-2.0-mini`) and `curl http://localhost:1234/health`.
- Expect about 11 s per short comment (four questions), about 19 s for 2500 characters and about 52 s for 10 000 (the eager mode without `--mode gguf` was 7–20 times slower). The server handles one request at a time and finishes a request even after the client gave up, so Development and compose prefetch one message.

### Jev through OpenRouter

1. Put the OpenRouter key in `.env` in the repository root (git-ignored, also kept out of images): `OPENROUTER_API_KEY=sk-or-…`.
2. Run the worker against Jev:
   - **Compose** – the override `docker-compose.jev.yml` sets the model, a 30 s timeout, the key from `.env` (the start fails if it is missing) and more concurrency (the cloud serves many requests at once):

     ```bash
     docker compose -f docker-compose.yml -f docker-compose.jev.yml up --build -d
     ```

   - **`dotnet run`** – the `jev` launch profile sets the same, except the key, which comes from the shell (PowerShell):

     ```powershell
     $line = Select-String -Path .env -Pattern '^OPENROUTER_API_KEY=(.*)'
     if (-not $line) { throw 'OPENROUTER_API_KEY is missing in .env' }
     # Compose strips quotes around .env values; do the same.
     $env:Model__SystemOne__ApiKey = $line.Matches[0].Groups[1].Value.Trim().Trim('"', "'")
     dotnet run --project src/CommentAnalysis.Worker --launch-profile jev
     ```

3. Check: `curl http://localhost:7461/health/ready` returns `Healthy` – readiness asks OpenRouter whether the model has a working provider, but does not check the key. The real check is a comment: the logs show `Comment analyzed: … by typesafe/jev-1.14-20261014@…` within a second (with a wrong key: `The model is unavailable`, and the comment waits).

Do not paste the output of `docker compose … config` or `docker inspect` anywhere: they show the key.

Back to Basal: `docker compose up --build -d` (compose without the override), or `dotnet run --project src/CommentAnalysis.Worker` in a new shell or after `Remove-Item Env:Model__SystemOne__ApiKey`. Analyses keep the model version they were made with, so both models' results stay apart in `comment_analyses`; a comment already analyzed by one model is analyzed again by the other only if its message is delivered again.

- The model is pinned to the dated snapshot the responses name (`typesafe/jev-1.14-20261014`); `typesafe/jev-1.14` would never match. A new snapshot (e.g. `jev-1.15-…`) changes the stored model version.
- Readiness needs no key, so a wrong key (401) or an empty account (402) is noticed by the first comment: comments then wait without using up their retries, and readiness turns unhealthy. The same happens when OpenRouter's provider is down (502/503).
- A rate limit (429) or an overload (529) also makes comments wait; the worker honours `Retry-After` up to 30 s. An overload makes readiness unhealthy for a minute; a rate limit does not.
- TypeSafe's own API (`https://api.typesafe.ai/v1/`, model e.g. `jev-1.14.0`) uses the same contract but has not been checked.

### Another model

Any server that implements `POST /v1/systemone` works (e.g. llama.cpp's server with a decision model): set `Model:SystemOne:BaseUrl`, `Model` (the name the responses carry), `Version` (pinned by you, since the API does not expose the weights), `Timeout` and, if needed, `ApiKey`. Readiness checks that the server lists the model (`GET models`, in Basal's or llama.cpp's format, or OpenRouter's model description). Before switching for real, run the quality report on the labelled sample and compare:

```bash
# Jev (the key from .env into the environment – never an argument, and not typed into the shell history); the results file lets a rerun resume
export SYSTEMONE_API_KEY="$(sed -n 's/^OPENROUTER_API_KEY=//p' .env)"
dotnet run scripts/quality-report.cs -- tests/data/polish-comments.json docs/quality/jev-1.14-results.json docs/quality/jev-1.14-report.md https://openrouter.ai/api/v1/ typesafe/jev-1.14-20261014 1.14-20261014
# Basal (defaults: http://localhost:1234/v1/, basal-2.0-mini, 2.0)
dotnet run scripts/quality-report.cs -- tests/data/polish-comments.json docs/quality/basal-2.0-mini-results.json docs/quality/basal-2.0-mini-report.md
# Side by side, at the current default policy, from the saved results
dotnet run scripts/compare-models.cs -- tests/data/polish-comments.json docs/quality/comparison.md basal-2.0-mini=docs/quality/basal-2.0-mini-results.json jev-1.14=docs/quality/jev-1.14-results.json
```

The questions themselves are constants in `SystemOneQuestions` (changing them changes the model version). The decision thresholds are configuration; they are not part of the model version, so analyses before and after a threshold change can only be told apart by time.

## Messages

JSON with camelCase names and enums as camelCase strings.

**In** – `CommentCreated`, routing key `comment.created`:

```json
{
  "commentId": "3f0c1d9e-6a43-4c1b-9a51-0d6c0f6b2a11",
  "postId": "8e2b7c44-1f5d-4a8e-b0c3-5d9e6f7a8b90",
  "authorId": "c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e5f",
  "content": "Świetny artykuł, dziękuję!",
  "createdAt": "2026-10-20T12:00:00Z"
}
```

**Out** – `CommentAnalyzed`, routing key `comment.analyzed`, AMQP `type` `CommentAnalyzed` and `message_id` the outbox row id (deduplicate on it):

```json
{
  "commentId": "3f0c1d9e-6a43-4c1b-9a51-0d6c0f6b2a11",
  "postId": "8e2b7c44-1f5d-4a8e-b0c3-5d9e6f7a8b90",
  "decision": "approve",
  "confidence": 0.99,
  "modelName": "static",
  "modelVersion": "static@1.0.0",
  "analyzedAt": "2026-10-20T12:47:56.1753487+00:00"
}
```

`decision` is `approve`, `reject` or `manualReview`; `confidence` is null for a comment that was not sent to the model (too long). The review reasons and signals are stored in the database but not (yet) in the event.

**Topology**, declared by the worker at startup (idempotently; readiness is unhealthy until it is done): the topic exchange `comments` (shared with the comment service), the quorum queue `comment-analysis.comment-created` (delivery limit 5, dead-lettering to the direct exchange `comment-analysis.dead-letter`) and the quorum dead-letter queue `comment-analysis.comment-created.dlq`. Queue arguments cannot change once a queue exists: after changing them locally run `docker compose down -v`; in production see [RabbitMQ policies](#rabbitmq-policies).

## Configuration

Standard .NET configuration: `appsettings.json`, then environment variables (`Section__Key`, e.g. `Consumer__Concurrency=4`). The shipped `appsettings.json` contains no connections and no model, so a deployment that forgets them fails at startup rather than running with defaults; every section is validated at startup.

| setting | default | meaning |
|---|---|---|
| **Model** | | |
| `Model:Provider` | – (required) | `SystemOne` or `Static` |
| `Model:SystemOne:BaseUrl` | – | API base including the version segment, e.g. `https://openrouter.ai/api/v1/`; https is required with an API key unless the host is local |
| `Model:SystemOne:Model` | – | the model name the responses carry |
| `Model:SystemOne:Version` | – | pinned version of the weights, part of the stored model version |
| `Model:SystemOne:ApiKey` | none | bearer key; from the environment or a secret store, never a committed file |
| `Model:SystemOne:Timeout` | `00:05:00` | one request (all four questions); at most 10 min |
| `Model:Static:Label`, `Confidence`, `Name`, `Version` | `Acceptable`, 0.99, `static`, `1.0.0` | the fixed answer of the static model |
| **DecisionPolicy** | | |
| `ApproveThreshold`, `RejectThreshold` | 0.85, 0.85 | minimum confidence for an automatic decision, in [0.5, 1] |
| `MaxToxicityForApprove`, `MaxSpamForApprove`, `MaxPersonalDataForApprove` | 0.35, 0.5, 0.5 | highest signal that still allows an approval, in [0, 1] |
| **Consumer** | | |
| `Concurrency` | 1 | messages processed at once by one replica |
| `Prefetch` | 4 | unacknowledged messages RabbitMQ sends ahead; ≥ `Concurrency`. Keep `Prefetch × (Model:SystemOne:Timeout + 30 s)` well below RabbitMQ's `consumer_timeout` (30 min) |
| `MaxMessageBytes` | 65536 | larger messages are dead-lettered unread |
| `MaxContentLength` | 1000 | longer comments go to manual review (`TooLong`) without the model |
| `MaxRetries`, `RetryDelay` | 3, `00:00:01` | retries of a failed message (linear, at most 30 s each) before the DLQ |
| **Database** | | |
| `ConnectionString` | – (required) | Npgsql connection string; outside Development it must have `SSL Mode=VerifyFull`, or the worker does not start |
| `AllowPlaintext` | false | allows any `SSL Mode` other than `VerifyFull` (also `Disable`) outside Development – a private network only (compose sets it); logged as a warning at startup |
| `MigrateOnStartup` | false | apply migrations at startup; for development and compose only |
| **RabbitMq** | | |
| `Uri` | – (required) | `amqps://user:password@host:5671/vhost`; outside Development `amqp://` stops the start |
| `AllowPlaintext` | false | allows `amqp://` outside Development – a private network only (compose sets it); logged as a warning at startup |
| `Exchange`, `InputQueue`, `InputRoutingKey`, `OutputRoutingKey` | `comments`, `comment-analysis.comment-created`, `comment.created`, `comment.analyzed` | topology names |
| `DeadLetterExchange`, `DeadLetterQueue` | `comment-analysis.dead-letter`, `comment-analysis.comment-created.dlq` | |
| `DeliveryLimit` | 5 | how many times a message may be returned after a crash; the next crash dead-letters it (queue argument, fixed once the queue exists; a policy can only lower it) |
| `ClientName`, `MaxReconnectDelay` | `comment-analysis-worker`, `00:00:30` | |
| **Outbox** | | |
| `BatchSize`, `PollInterval`, `PublishTimeout` | 100, `00:00:01`, `00:00:10` | publishing |
| `Retention`, `CleanupInterval` | `7.00:00:00`, `01:00:00` | how long published rows are kept |
| **Host** | | |
| `ShutdownTimeout` | `00:00:20` | time for in-flight messages at shutdown; keep it below the orchestrator's grace period (compose: 45 s) |
| **Telemetry** | | |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | none | enables the OTLP metrics export (see below) |

Comment content and author ids are personal data: the worker never logs them at Information level or above.

## Health and metrics

- `GET /health/live` – the process is up (no dependency checks, so an outage does not restart the container). The image's `HEALTHCHECK` uses it.
- `GET /health/ready` – PostgreSQL, RabbitMQ (topology declared), the consumer and the decision model; 503 while any of them fails.

The worker records metrics with `System.Diagnostics.Metrics` (meter `CommentAnalysis.Worker`: decisions, review reasons, model duration, retries, requeues, dead-letters, published events and the outbox lag) and exports them over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` (or `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`) is set; without it nothing is exported. The other standard settings apply (`OTEL_EXPORTER_OTLP_PROTOCOL` – gRPC by default, `OTEL_EXPORTER_OTLP_HEADERS` for a collector's key, from a secret store – `OTEL_METRIC_EXPORT_INTERVAL`, `OTEL_SERVICE_NAME`, default `comment-analysis-worker`). An endpoint that is not an absolute http(s) URI stops the start, but a reachable-looking wrong one does not: export errors are not in the worker's logs (only in the OpenTelemetry SDK's self-diagnostics).

To see the metrics locally in the Aspire Dashboard (compose passes `WORKER_OTLP_ENDPOINT` on, so a shell's own `OTEL_EXPORTER_OTLP_ENDPOINT` does not leak into the container):

```bash
WORKER_OTLP_ENDPOINT=http://aspire-dashboard:18889 docker compose --profile observability up -d
```

Add the same `-f` files you started with (e.g. `-f docker-compose.yml -f docker-compose.static.yml`), otherwise compose recreates the worker with the default model. Then open http://localhost:18888 → Metrics → `comment-analysis-worker`. Alert on `comment_analysis.outbox.oldest_pending_age` with max across replicas, and treat a missing value as a problem too (the worker reports none while it cannot read the outbox). Logs go to the console as JSON (one object per line, UTC timestamps).

## Scaling and load test

The worker is stateless, so it scales by running more replicas against the same queue and database (see the guarantees above). Raise `Consumer:Concurrency` for a model that serves requests in parallel (Jev); keep it at 1 for the local Basal server.

The load test needs a fast model – the static one or Jev; Basal takes about 11 s per comment. Do not change the model during a run.

```bash
# Two replicas of the static model (no host port: they would clash on 7461; health is in `docker compose ps`)
docker compose -f docker-compose.yml -f docker-compose.static.yml -f docker-compose.replicas.yml up --build -d
# Publish comments, stop or kill replicas meanwhile if you like, then check that nothing was lost or analyzed twice
dotnet run scripts/load-test.cs -- publish 1000 load-test-run.json
dotnet run scripts/load-test.cs -- verify load-test-run.json      # exit 0 passed, 2 failed, 3 timed out; rerun after 2 or 3
# Back to one replica of the default model
docker compose up --build -d
```

Results of the H2 runs (stopping and killing replicas under load, with the static model and with Jev) are in the implementation plan.

## Deploying to production

**Image.** `src/CommentAnalysis.Worker/Dockerfile` builds a non-root image listening on port 7461 (health checks only; the worker serves no other HTTP). Give it:

- `Model__Provider`, the `Model__SystemOne__*` settings and the API key from a secret store;
- `Database__ConnectionString` and `RabbitMq__Uri`, also from a secret store, both with TLS – the worker refuses to start without it outside Development: `Host=…;Database=comment_analysis;Username=…;Password=…;SSL Mode=VerifyFull` (plus `Root Certificate=…` for a private CA) and `amqps://user:password@host:5671/vhost`;
- a termination grace period above `Host:ShutdownTimeout` plus about 20 s for closing the connections (compose uses 45 s).

**Database migrations.** Leave `Database:MigrateOnStartup` off and apply migrations before deploying a new version, with an EF Core migration bundle – a single executable that needs no SDK on the target. Build it for the host that runs it (here a linux-x64 deploy job or container, so it does not run on the Windows machine that built it):

```bash
dotnet tool restore
dotnet ef migrations bundle --project src/CommentAnalysis.Infrastructure --startup-project src/CommentAnalysis.Infrastructure --configuration Release --self-contained -r linux-x64 --output efbundle
# On the deploy host, with the connection string injected from the secret store (not on the command line, where it
# would land in the shell history and the process list):
Database__ConnectionString="$(cat /run/secrets/migrator-connection-string)" ./efbundle
```

Without `--connection` or `Database__ConnectionString` the bundle falls back to the local compose database (`localhost:5433`), so always set one – with `SSL Mode=VerifyFull` like the worker's (the bundle does not enforce it, and it connects as the schema owner). It applies only the missing migrations (none on an up-to-date database) and takes a lock, so two runs cannot interfere. It can run with a user that may change the schema, while the worker's own user only needs to read, write and delete rows in its two tables. A migration's `Down` may lose data (e.g. dropping analyses made without the model), so roll back by deploying the previous version only after checking it.

**RabbitMQ.** The worker declares its topology at every start (exchanges, queues, bindings – see [Messages](#messages)), even when it already exists, so its user needs the *configure* permission on `comments`, `comment-analysis.*` and the dead-letter queue, *write* on `comments` and on the default exchange (retries are republished straight to the input queue), and *read* on the input queue. Without configure the declaration fails, readiness stays unhealthy and nothing is consumed.

**RabbitMQ policies.** The worker declares its queues with fixed arguments, and redeclaring with different ones fails (`PRECONDITION_FAILED`), so tune queues that already exist with a policy: it applies to an existing queue and to the messages in it, and the worker's declaration stays valid. Checked on RabbitMQ 4.3.6 (the delivery limit also by an integration test):

| Key | Policy vs the worker's argument | Use |
|---|---|---|
| `delivery-limit` | the lower value wins: a policy can lower the limit (5), never raise it | lower it to stop a crash loop sooner; to raise it, change `RabbitMq:DeliveryLimit` and recreate the queue (drain it first) |
| `max-length`, `max-length-bytes` | the worker sets none | cap the input queue; with the worker's `reject-publish` the broker nacks publishes once the queue is over the cap (one message over, as quorum queues allow), so the comment service must use publisher confirms and retry. The worker's own retries are nacked too: it requeues the original uncounted and processes it again (another model call after the retry delay) until the queue is below the cap, so a comment that always fails cannot reach the DLQ meanwhile (metric `comment_analysis.messages.requeued`, reason `retry_publish_failed`) |
| `overflow` | **the policy wins** | never set it: `drop-head` discards the oldest comments to the DLQ, and because at-least-once dead-lettering requires `reject-publish`, RabbitMQ falls back to at-most-once for every message dead-lettered from the queue (crashes, exhausted retries, malformed messages) |
| `dead-letter-exchange` | the argument wins | – (the worker's DLX stays in use) |
| `delivery-limit` on the DLQ | the DLQ has no argument, so RabbitMQ 4's implicit limit applies: a message is **dropped** on its 21st failed delivery (no dead-letter target) | set an explicit limit (or a dead-letter target) by policy before any tool replays the DLQ |

The limit counts failed deliveries only (a channel or connection closed without an ack); a nack with requeue does not count. With limit *N* a message is delivered *N* + 1 times and dead-lettered on the last crash. For example, to dead-letter a message on its third crash (two redeliveries):

```bash
rabbitmqctl set_policy comment-analysis-input '^comment-analysis\.comment-created$' '{"delivery-limit":2}' --apply-to queues
```

A policy matches by a pattern, and only one policy (the highest priority) applies to a queue, so merge these keys into an existing policy that already matches the queue instead of adding a second one. An operator policy (`set_operator_policy`) applies on top of it; for numeric limits such as `max-length` and `delivery-limit` the lowest value wins.

**Images** are pinned: the Dockerfile's base images to a patch version and digest (refresh the digests at least monthly and on every .NET patch release – a tag is also re-pushed with OS security fixes; the Dockerfile says how), compose and the integration tests to patch versions of PostgreSQL and RabbitMQ.

**Not done yet (phase H4):** a way to verify the topology without declaring it.

## Development

```bash
dotnet build                                   # warnings are errors
dotnet test                                    # unit and integration tests (Testcontainers: Docker must run)
dotnet format --verify-no-changes              # style, including naming rules the build does not check
dotnet ef migrations add <Name> --project src/CommentAnalysis.Infrastructure --startup-project src/CommentAnalysis.Infrastructure --output-dir Persistence/Migrations
```

The solution has three projects – `Core` (domain, policy, contracts; no infrastructure), `Infrastructure` (models, EF Core, RabbitMQ) and `Worker` (the host) – and two test projects. Work is planned and recorded increment by increment in [docs/implementation-plan.md](docs/implementation-plan.md); [CLAUDE.md](CLAUDE.md) summarises the conventions.
