# Comment Analysis Worker – Implementation Plan

## Goal
A .NET 10 worker that consumes newly created user comments from RabbitMQ, evaluates each one with a decision model, and decides **Approve**, **Reject** or **ManualReview**. For every comment it stores the decision, the model's **confidence** (score) and the **model version** in PostgreSQL. It then publishes a `CommentAnalyzed` event. The worker is stateless and runs in containers.

The model is hidden behind `IDecisionModel`:
- **Default (phase S, in progress):** a decision model (Basal 2.0 mini) queried through the TypeSafe-compatible `POST /v1/systemone` API, as implemented by llama.cpp's server and by a local Basal server. One request asks four typed questions (`choice`, `score`, `noul`); the answers come from the model's output distribution, not from generated text. The same client can point at TypeSafe's cloud Jev (`https://api.typesafe.ai/v1/systemone`).
- **Superseded:** the LLM behind LM Studio's OpenAI-compatible chat completions API (phase F), which asked the model to rate its own confidence.
- **Optional:** a local ONNX toxicity classifier (phase I), if a non-generative model is preferred later.

## Ground rules
- All code, comments, commit messages and documentation are in English.
- Small increments. Each one builds, passes its tests and ends with **one commit**.
- After each increment I stop and report what changed, so it can be reviewed before moving on.
- Nothing in an increment depends on code from a later increment.
- Every external dependency (DB, broker, model) sits behind an abstraction, so earlier increments can be tested without it.

## Target architecture
```
RabbitMQ exchange "comments" (topic)
  └─ queue "comment-analysis.comment-created"  (routing key comment.created, dead-letter → comment-analysis.comment-created.dlq)
        │ CommentCreated { commentId, postId, authorId, content, createdAt }
        ▼
  CommentCreatedConsumer
        ├─ skip if analysis (commentId, modelVersion) already exists
        ├─ IDecisionModel → ModelPrediction
        ├─ DecisionPolicy → Decision + Confidence
        ├─ one DB transaction: comment_analyses row + outbox_messages row
        └─ ack after commit (retry / DLQ on failure)
  OutboxPublisher → CommentAnalyzed { commentId, postId, decision, confidence, modelName, modelVersion, analyzedAt }
                    routing key comment.analyzed
```

### Decision policy
- The model returns a `ContentLabel` (`Acceptable` / `Unacceptable`) and a confidence for it, plus optional `ModerationSignals` (toxicity, spam, personal data, each in [0, 1]). The `/v1/systemone` model maps the `verdict` choice `approve` → Acceptable and `reject` → Unacceptable, with the probability of the chosen option as the confidence. ONNX maps `non-toxic` → Acceptable and `toxic` → Unacceptable with `confidence = max(pToxic, 1 - pToxic)` and no signals.
- `Unacceptable` and `confidence >= RejectThreshold` (default 0.90) → Reject
- `Acceptable` and `confidence >= ApproveThreshold` (default 0.85) and no signal above its limit (`MaxToxicityForApprove` 0.35, `MaxSpamForApprove` 0.5, `MaxPersonalDataForApprove` 0.5) → Approve
- anything else → ManualReview, with the review reasons (`LowConfidence`, `Toxicity`, `Spam`, `PersonalData`) stored next to the decision. Signals only ever stop an approval; they never reject.

### Model version
For `/v1/systemone`: `{Model}@{Version}+{first 12 hex chars of SHA-256 of the framing and the four questions (types, instructions, criteria)}`, the same scheme as for the LLM. For the LLM: `{Model}@{Version}+{first 12 hex chars of SHA-256 of the instructions and the response schema}`. `Version` is pinned in config, because the API does not expose the weights; the hash makes any prompt change a new version. For ONNX: `{Name}@{Version}+{first 12 hex chars of SHA-256 of the model file}`. For Jev, the `model` field returned by the API (e.g. `jev-1.13.0`).

### Solution layout
```
CommentAnalysis.slnx
Directory.Build.props / Directory.Packages.props / .editorconfig
src/CommentAnalysis.Core            – domain, contracts, policy (no infrastructure deps)
src/CommentAnalysis.Infrastructure  – ONNX, Jev, EF Core/Postgres, RabbitMQ
src/CommentAnalysis.Worker          – host, consumer, outbox publisher, health endpoints, Dockerfile
tests/CommentAnalysis.Core.Tests
tests/CommentAnalysis.IntegrationTests  – Testcontainers (Postgres, RabbitMQ)
scripts/                            – model export
docker-compose.yml, README.md, docs/
```

---

## Increments

Status legend: `[ ]` todo · `[~]` in progress · `[x]` done

### Phase A – Foundation

**A1. Repository skeleton** `[x]`
- Done: `git init`, `.gitignore`, `CommentAnalysis.slnx`, five empty projects.
- Todo: `Directory.Build.props` (net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors), `Directory.Packages.props` (Central Package Management), `.editorconfig`, project references (Infrastructure → Core, Worker → Infrastructure, tests → their targets), and a placeholder `README.md`.
- Done when: `dotnet build` and `dotnet test` pass with zero warnings.

**A2. Core domain types** `[x]`
- `Decision` enum, `ContentLabel` enum (model-agnostic label), `ModelPrediction` (label, confidence validated to [0, 1], raw probabilities), `ModelInfo` (name, version).
- Done when: the project compiles and validation tests pass.

**A3. Decision policy** `[x]`
- `DecisionPolicyOptions` (thresholds, validated to [0.5, 1] via `GetValidationErrors()`, which the host will reuse for `ValidateOnStart`) and `DecisionPolicy.Decide(ModelPrediction)`.
- Unit tests: each outcome, exact boundary values, and invalid options.
- Done when: all policy tests pass.

**A4. Model abstraction and analyzer** `[x]`
- `IDecisionModel` (`ModelInfo Info`, `Task<ModelPrediction> EvaluateAsync(string content, CancellationToken)`).
- `CommentAnalyzer`, which calls the model and the policy and returns an `AnalysisResult` (decision, confidence, model info, duration).
- A `FakeDecisionModel` in the test project.
- Done when: the analyzer unit tests pass.

**A5. Message contracts** `[x]`
- `CommentCreated` and `CommentAnalyzed` records (all properties `required`), `CommentAnalyzed.From(comment, result)`, and `MessageSerializer` with shared options: camelCase, enums as camelCase strings (integers rejected), nullable annotations respected, unknown properties ignored.
- Tests: round-trip, JSON shape, and malformed payloads (missing or null fields, bad GUID, invalid JSON) throwing `JsonException` – the basis for DLQ routing in D3.

### Phase B – Runnable host

**B1. Minimal worker host** `[x]`
- `Program.cs`: options binding with `ValidateOnStart`, structured console logging, a `/health/live` endpoint, and a `Model:Provider` setting. A `Static` provider returns a fixed prediction, so the worker runs without a real model.
- Implemented as `AddCommentAnalysis(configuration)` in Infrastructure (`DependencyInjection.cs`). `Model:Provider` has no default, so a missing value fails at startup. Logs are JSON in production and single-line text in Development. Integration tests use `WebApplicationFactory<Program>`.
- After the first full review: `Model:Provider` is set only in `appsettings.Development.json`, compose and tests – never in the shipped `appsettings.json` (which is also the only settings file in the image). The model is created at startup (`DecisionModelWarmup`), and the Static model logs a warning outside Development.
- Done when: `dotnet run` starts and `/health/live` returns 200.

**B2. Container** `[x]`
- Multi-stage `Dockerfile` (sdk:10.0 → aspnet:10.0, non-root user, port 7461) and a `docker-compose.yml` containing only the worker.
- Runtime images have no curl/wget, so `HEALTHCHECK` runs `dotnet CommentAnalysis.Worker.dll healthcheck`, which probes `/health/live`. The worker listens on 7461 in the container and locally (compose maps 7461 → 7461): ports like 8080 or 5000 are usually taken by the main applications. Invalid configuration ends the container with a non-zero code (currently 139, from the unhandled startup exception) after the host logs "Hosting failed to start".
- Done when: `docker compose up --build` starts and the health check passes.

### Phase C – Persistence (PostgreSQL)

**C1. DbContext and initial migration** `[x]`
- EF Core 10 + Npgsql, `AnalysisDbContext`, and the `comment_analyses` table with `UNIQUE(comment_id, model_version)`.
- First migration, applied at startup when `Database:MigrateOnStartup=true`. Postgres is added to compose.
- Columns: `id, comment_id, post_id, decision, label, confidence, probabilities (jsonb), model_name, model_version, analyzed_at, duration_ms`, plus a check constraint `confidence` ∈ [0, 1]. Snake_case names are configured explicitly (no naming-convention package). `dotnet ef` is a local tool (`dotnet-tools.json`) and uses a design-time factory, so it does not start the worker host. Migrations are excluded from analyzers via `.editorconfig`. On a brand-new database EF logs one "Failed executing DbCommand" error while it creates its history table; this is expected EF behaviour.
- Test: the EF model has no changes missing from migrations (`HasPendingModelChanges`).
- Done when: the worker in compose creates the table.

**C2. Analysis store and idempotency** `[x]`
- `IAnalysisStore` with `ExistsAsync(commentId, modelVersion)` and `SaveAsync(result)`, implemented with EF.
- Integration tests on a Postgres Testcontainer: save, then detect the duplicate. Also check that the unique constraint holds under a race.
- `IAnalysisStore` lives in Core; `EfAnalysisStore` (Infrastructure) maps a unique-violation on `ux_comment_analyses_comment_id_model_version` to `SaveOutcome.AlreadyExists` instead of throwing, so a lost race is not an error. Tests share one Postgres container per collection (`PostgresFixture`) and isolate data with fresh comment ids; the race test runs 8 concurrent saves.

**C3. Readiness check for Postgres** `[x]`
- `/health/ready` reports Postgres status.
- EF Core `AddDbContextCheck` tagged `ready`. `/health/ready` returns 503 when Postgres is down while `/health/live` stays 200, so a database outage takes the worker out of rotation without restarting it. The container `HEALTHCHECK` keeps probing liveness only.

### Phase D – Messaging (RabbitMQ)

**D1. Connection and topology** `[x]`
- `RabbitMqOptions`, a singleton connection provider (RabbitMQ.Client 7.x, async API), and a `TopologyInitializer` that declares the exchange, the queue, the DLX and the DLQ. RabbitMQ is added to compose, and readiness now also checks RabbitMQ.
- `RabbitMqTopologyInitializer` is a `BackgroundService` that retries with exponential back-off (up to `RabbitMq:MaxReconnectDelay`), so the host starts even when RabbitMQ is down; consumers will await `WaitUntilReadyAsync`. After the first connection the client's automatic recovery reconnects. Both queues are quorum queues; the input queue dead-letters to `comment-analysis.dead-letter` (direct) → `comment-analysis.comment-created.dlq`. Queue arguments cannot change on an existing queue (PRECONDITION_FAILED), so later tuning goes through RabbitMQ policies. Integration tests isolate topology per test with unique exchange/queue names. Verified in compose: stopping RabbitMQ turns `/health/ready` to 503 and it recovers on its own after restart.
- Changing queue arguments breaks existing queues with PRECONDITION_FAILED (readiness then reports a configuration error). Locally: `docker compose down -v` or delete the queue in the management UI; in deployed environments use RabbitMQ policies (H4).
- After the first full review: the input queue uses at-least-once dead-lettering (`x-dead-letter-strategy`, which requires `x-overflow: reject-publish`) and `x-delivery-limit` = `RabbitMq:DeliveryLimit` (default 5). In RabbitMQ 4 only consumer failures (channel or connection closed without an ack) count towards the limit; an explicit nack/reject with requeue does not. Non-recoverable broker errors (authentication, 403 access refused, 406 precondition failed) are logged as errors and reported by readiness as a configuration error. The connection closes with a 5 s timeout and is aborted if that fails.
- Done when: the queues are visible in the management UI after startup.

**D2. Consumer – happy path** `[x]`
- `CommentCreatedConsumer` as a `BackgroundService`: manual ack, configurable prefetch, a new DI scope per message, and a log scope carrying `commentId`. It deserializes the message, skips it if already analyzed, analyzes it, saves the result and then acks.
- Graceful shutdown: on stop, cancel the consumer (no new deliveries), let the in-flight message finish and ack, then close the channel. Align timeouts so the host finishes before the orchestrator kills it: `HostOptions.ShutdownTimeout` (`Host:ShutdownTimeout`, 20 s) < compose `stop_grace_period` (40 s) < k8s `terminationGracePeriodSeconds`. Docker's default grace period is only 10 s, while the .NET host waits up to 30 s by default.
- Test: stopping the host while a message is being processed still produces the DB row and acks the message.
- Use a processing token for the in-flight message that is *not* the host's `stoppingToken` (it is cancelled at shutdown and would abort the save); bound it by `ShutdownTimeout` instead.
- Logging: the log scope carries `commentId` only. Comment `content` and `authorId` are personal data and must not appear in logs at Information level or above, nor in exception messages.
- Integration test (Postgres + RabbitMQ containers, fake model): publishing a message produces a DB row.
- Implemented: `CommentProcessor` (Core) does skip-if-analyzed → analyze → save, and is unit-tested with in-memory fakes. `CommentCreatedConsumer` (Worker) waits for the topology, uses its own channel with `Consumer:Concurrency` (dispatch concurrency, default 1) and `Consumer:Prefetch` (default 4), acks after the save, rejects malformed JSON to the DLQ (early part of D3), and nacks with requeue on unexpected errors (bounded retries come in D3). Shutdown: cancel consumer → wait for in-flight handlers (semaphore) → close channel; the processing token is cancelled only when `Host:ShutdownTimeout` (20 s) expires; compose `stop_grace_period` is 40 s (see below). `StopAsync` is idempotent because WebApplicationFactory and `RunAsync` both stop the host. Integration tests check unacked counts with `rabbitmqctl` inside the RabbitMQ container, since a passive queue declaration only reports ready messages. Verified in compose: a published comment is stored, logs carry only `CommentId` (no content), and `docker compose stop` exits 0 in about 2 s.
- After the D2 review: `ExecuteAsync` is a loop that recreates the channel and consumer (with back-off) when the broker closes the channel or cancels the consumer on a live connection (e.g. the queue was deleted, `consumer_timeout`); a connection failure is left to the client's automatic recovery, which restores the original consumer (verified: still exactly one consumer after `rabbitmqctl close_all_connections`). `/health/ready` also reports whether the consumer is registered. Ack/nack/reject on a closed channel is logged as a warning instead of escaping into the client's dispatcher, and callback exceptions are logged. A handler waits for a slot on a token cancelled as soon as shutdown starts, so a delivery that arrives during shutdown is left unacked (and redelivered) instead of blocking the drain. A broker-side cancel is detected by overriding `HandleBasicCancelAsync` (`InputQueueConsumer`), because the unregistered event also fires on connection failures. A consumer start that races with `StopAsync` cancels itself. Shutdown budget: the client did not always honour its own close timeout (a 30 s hang was observed), so every cancel, close, abort and dispose step is capped with `WaitAsync`: `Host:ShutdownTimeout` 20 s + consumer channel close/abort/dispose 3 × 2 s + RabbitMQ connection close/abort 2 × 3 s = 32 s < compose `stop_grace_period` 40 s; size k8s `terminationGracePeriodSeconds` the same way. Tests cover concurrency 2 during shutdown, a transient model failure (nack + requeue), a broker-side consumer cancel and a dropped connection.

**D3. Failure handling** `[x]`
- Malformed JSON or failed validation → straight to the DLQ.
- Input limits: reject messages whose body exceeds a configured size (check the AMQP body length before deserializing) and comments whose `content` exceeds `Consumer:MaxContentLength` – a multi-megabyte comment would otherwise be a cheap DoS on the model (F2).
- Handled transient errors (e.g. database unavailable) → retried by the worker itself: republish a copy with an incremented `x-retry-count` header (with publisher confirms) before acking the original, up to `Consumer:MaxRetries`, then reject to the DLQ. Note: in RabbitMQ 4 a nack/reject with requeue does not count towards `x-delivery-limit` (verified), so the queue's delivery limit only covers consumer crashes (D1).
- Producer-controlled values that reach logs (e.g. `BasicProperties.MessageId`) are truncated and stripped of control characters (log forging; D2 review).
- Integration tests for each path.
- Implemented in two steps. D3a: `Consumer:MaxMessageBytes` (default 64 KiB, checked before deserializing) and `Consumer:MaxContentLength` (default 10 000 characters, checked before the model) dead-letter oversized input; the message id is sanitized before logging. D3b: any processing failure is retried by republishing a copy (default exchange → input queue, `mandatory`, publisher confirms on the consumer channel) with `x-retry-count + 1` after a linear delay (`Consumer:RetryDelay` × attempt, default 1 s, max 30 s because the wait holds a processing slot), and the original is acked only after the confirm. At `Consumer:MaxRetries` (default 3) the message is rejected to the DLQ, where it keeps its retry count. If the republish is not confirmed, the original is nacked with requeue (uncounted). Shutdown interrupts the delay and leaves the message unacked. All failures are treated as retryable for now; G2 adds non-transient errors (e.g. a Jev model mismatch) that skip retries.
- After the D3 review: the retry copy drops `CC`/`BCC` (otherwise a producer could make the worker route copies into any queue in the vhost) and the broker's dead-lettering and delivery-count headers; a single retry wait is capped at 30 s, far below `consumer_timeout`; waiting for the confirm is cancelled by the shutdown timeout, and a confirm lost with the channel falls back to nack + requeue. Deferred (LOW): integration tests for an unconfirmed republish and for shutdown during the retry delay.

### Phase E – Outgoing events

**E1. Transactional outbox – write side** `[x]`
- `outbox_messages` table and migration. `CommentAnalyzed` is written in the same transaction as the analysis row.
- Test: both rows exist after processing, or neither does.
- Implemented: `IAnalysisStore.SaveAsync` keeps its signature; `EfAnalysisStore` adds the analysis row and an `OutboxMessageEntity` (`id` UUIDv7 – later the AMQP message id, `type` = message type name, `payload` jsonb, `created_at` = analysis time, `published_at` null) in one `SaveChanges`, i.e. one transaction. A duplicate analysis rolls back both rows, so a redelivery never enqueues a second event; a test appends a failing statement after both inserts and checks that neither row remains. `type` is an explicit constant (`CommentAnalyzed.MessageType`), not the CLR class name. A partial index on `created_at WHERE published_at IS NULL` serves the publisher. Cleanup of published rows is part of E2.

**E2. Outbox publisher** `[x]`
- A `BackgroundService` that polls unpublished rows (`FOR UPDATE SKIP LOCKED`, so multiple replicas are safe), publishes with publisher confirms and marks the rows as published.
- Use a separate AMQP connection for publishing (distinct `ClientProvidedName`): RabbitMQ blocks publishing connections under flow control or resource alarms, which must not stall the consumer's acks. Make `RabbitMqConnection` support named connections ("consumer", "publisher") before this step.
- End-to-end integration test: `CommentCreated` in → `CommentAnalyzed` out.
- Notes from the E1 review: the payload is jsonb, so key order, whitespace and number formatting are normalized – publish it as is, and consumers must not rely on exact bytes (deduplicate on the message id). Map `type` to a routing key explicitly and fail loudly on an unknown type. Clean up published rows (e.g. delete after a retention period; may need an index on `published_at`).
- Implemented: `RabbitMqConnection` is named (`ClientProvidedName` = `{ClientName}:{name}`); the consumer connection is resolved without a key and the publisher one as a keyed singleton (`publisher`). `OutboxPublisher` (Worker) claims up to `Outbox:BatchSize` (100) rows ordered by `created_at` with `FOR UPDATE SKIP LOCKED` in one transaction, publishes them one by one to the `comments` exchange with publisher confirms (`message-id` = row id, `type`, persistent, not `mandatory` – events nobody subscribes to are dropped instead of piling up), marks the confirmed ones and commits; the first failure stops the batch, and the confirmed part is still committed. A full batch is followed immediately by the next one, otherwise it waits `Outbox:PollInterval` (1 s). Failures back off exponentially (1 s → 30 s) and recreate the channel. `Outbox:PublishTimeout` (10 s) bounds a batch (e.g. a blocked connection). Only rows of known types are claimed (`type = ANY(...)`), so a row this version cannot route (e.g. written by a newer version during a rolling deploy) never takes up batch slots; the cleanup reports how many such rows wait. Cleanup every `Outbox:CleanupInterval` (1 h) deletes rows published more than `Outbox:Retention` (7 days) ago, in batches of 1000, using a partial index on `published_at`. Delivery is at least once (a crash between the confirm and the commit republishes), ordering is not guaranteed. Shutdown budget with the second connection: 20 s + 3 × 2 s + 2 connections × 2 × 3 s = 38 s < compose `stop_grace_period` 45 s.
- After the E2 review: the channel is opened before the transaction, so no row locks are held while connecting; cleanup has its own error handling and runs even while publishing fails; recording a partly published batch is bounded (5 s) and keeps the original failure in the logs. Deferred: concurrent publishes within a batch (throughput), an outbox-lag metric (age of the oldest unpublished row) for H2, and a two-replica test (H3 verification already covers it in compose).

### Phase F – Real model (local LLM via an OpenAI-compatible API) – superseded by phase S

Findings from a manual check against LM Studio (`basal-2.0-mini`, `POST /v1/chat/completions` with `response_format: json_schema`): the label was correct for an insulting Polish comment, the response `model` is the requested id, `logprobs` is `null`, one call took about 6 s, and LM Studio enforced the structure and the enum but **not** the numeric range (it answered `"confidence": 60` for a [0, 1] schema). So the model is asked for an integer 0–100, and the worker validates the range itself.

**F1. LlmDecisionModel** `[x]`
- `Model:Provider=Llm` with `Model:Llm` options: `BaseUrl` (e.g. `http://localhost:1234/v1/`), optional `ApiKey`, `Model` (e.g. `basal-2.0-mini`), pinned `Version`, `Instructions` (system prompt, with a default), `Timeout` (60 s), `MaxTokens`.
- Sends the comment as the user message with `temperature: 0` and a strict JSON schema (`label`: `acceptable` | `unacceptable`, `confidence`: integer 0–100). Validates the response: the `model` must equal the configured one, the content must match the schema and the range – anything else is an error (retried by the consumer, then the DLQ). `ModelPrediction.Confidence` = confidence / 100.
- The version is computed once (see Model version). The comment content is never logged.
- Tests with a stubbed `HttpMessageHandler`: request shape, mapping, the version hash, and invalid responses (wrong model, malformed JSON, out-of-range confidence, HTTP errors).
- Implemented: `LlmDecisionModel` (Infrastructure, `Models/Llm`) uses the named `HttpClient` `llm` from `IHttpClientFactory` (timeout `Model:Llm:Timeout`, redirects disabled). The comment is sent JSON-encoded (`{"comment": ...}`) so it cannot imitate the prompt; the framing, instructions and schema are hashed into the version, e.g. `basal-2.0-mini@2.0+cb3e34c601af`. A confidence is accepted only as a whole number 0–100 (`85.0` passes, `0.6` does not – it suggests another scale), a `finish_reason` other than `stop` is an error, and `LlmResponseException` messages never contain the answer. An `ApiKey` requires https unless the server is local (loopback or `host.docker.internal`). `System.Net.Http.HttpClient` logs at Warning.
- Live check through the class against LM Studio (5–10 s per comment): labels were right for a polite, an insulting and an injection comment ("ignore previous instructions…" plus an insult got `acceptable`, but with confidence 0–0.5, so ManualReview), while a polite disagreement got `unacceptable` 0–0.5. With the JSON framing the clear cases dropped from 0.95–1.00 to 0.80, i.e. ManualReview at the default thresholds – prompt and threshold tuning is F3.
- After the F1 review: JSON framing as part of the version, redirects off, https for an `ApiKey`, quieter HttpClient logs, and tests for the validator, timeouts and an answer echoing the comment. Deferred to F2: an unavailable model (see below).

**F2. Wire the LLM into the host** `[x]`
- Development settings and compose use the LLM provider (`host.docker.internal:1234` from the container, which needs LM Studio's "Serve on Local Network"). Readiness checks that the configured model is listed by `GET /v1/models`.
- **An unavailable model must not drain the queue into the DLQ** (F1 review, MEDIUM): today every failure takes the counted retries (1 s, 2 s, 3 s), so while LM Studio is closed, restarting or has unloaded the model, each message is dead-lettered within seconds; a model mismatch (another model loaded) never recovers. Treat "model unavailable" (connection errors, 5xx/429, timeouts, a model mismatch) separately from bad answers: requeue without counting after a back-off, or pause consuming while the model check fails. Bad answers (`LlmResponseException`) keep the counted retries and then the DLQ. Also check how LM Studio answers the first call after an idle unload (JIT loading: an error or a long delay beyond the timeout).
- Implemented: `ModelUnavailableException` (Core) marks failures that say nothing about the comment. `LlmDecisionModel` throws it for an unreachable server, its own timeout, 404/408/429/5xx and an answer from another model; other statuses (e.g. 400) and invalid answers stay counted failures. The consumer returns such a message to the queue with a nack (uncounted – RabbitMQ 4 does not count requeues towards the delivery limit) after a back-off shared by all handlers (1 s doubling to 30 s, reset by the next processed comment), so the worker effectively pauses while the model is down and nothing reaches the DLQ. Readiness has a `model` check (`DecisionModelHealthCheck`): for the LLM, `GET models` within 3 s must list the configured model; other providers are always healthy. `appsettings.Development.json` and compose use the LLM (`host.docker.internal`, with `extra_hosts: host-gateway` for Docker Engine on Linux). EF Core command logs moved to Warning (the outbox polls every second).
- Manual check in compose against LM Studio: readiness Healthy from the container, three Polish comments analyzed (4–8 s each) and their events published; after `lms unload`, the next comment reloaded the model on demand and took 11 s without an error, so JIT loading fits the 60 s timeout. LM Studio lists downloaded models whether loaded or not, so readiness proves the model can be served, not that it is warm. The model is loaded with a 4096-token context (see F3).
- After the F2 review: a timeout or an error status is confirmed by `GET models` before it counts as an outage – if the server still lists the model, the failure is attributed to the comment (counted retries, then the DLQ), so a comment that always times out or always gets a 500 cannot wait forever. 401/403/3xx (configuration) also mean the model is unavailable. An outage seen by a comment (including another model answering, which the model list does not show) keeps readiness unhealthy for a minute or until the model answers again. The back-off is doubled atomically and reset only when the model actually answered (not by a skipped duplicate). Deferred: a test for shutdown during the unavailable back-off. Checked: LM Studio answers a chat request for an unknown model id with 200 from the currently loaded model (and that model's id), so the response model check is what catches a wrong `Model:Llm:Model`; how it answers when the model cannot be loaded (e.g. out of memory) is still unknown.
- Manual end-to-end check (see Verification).

**F3. Polish quality check** – superseded by S4
- A small labelled sample of Polish comments in `tests/data/`, plus a report (opt-in test or console tool, run against LM Studio) showing the decision distribution, accuracy and latency at the default thresholds.
- Check the loaded context window against `Consumer:MaxContentLength` (10 000 characters): LM Studio often loads models with 4096 tokens, so a long comment may be rejected (400) or truncated (`finish_reason: length`), which is deterministic and burns all retries before the DLQ.
- Outcome: tune the instructions and thresholds, or pick a different model. **Decision point for the user.**

### Phase S – Decision model via `/v1/systemone` (replaces the LLM of phase F)

The user preferred the TypeSafe-compatible `/v1/systemone` API (llama.cpp `tools/server/README.md`, "TypeSafe-compatible API endpoints") to prompting a chat model, and asked to use all three question types. `LlmDecisionModel` is replaced; F1/F2's handling of an unavailable model (probe, uncounted requeue, readiness) is kept.

Questions sent in one request (`state` = the comment as a plain string; question texts are constants in code and part of the model version):

| id | type | role |
|---|---|---|
| `verdict` | choice `approve` / `reject` | the label; confidence = probability of the chosen option |
| `toxicity` | score, 4 levels (none, mild, offensive, severe) | expected level ÷ 3 → `ModerationSignals.Toxicity` |
| `spam` | noul | probability of true → `ModerationSignals.Spam` |
| `personal_data` | noul | probability of true → `ModerationSignals.PersonalData` |

**S0. Live contract check** `[x]`
- Checked against the user's Basal server on `http://localhost:1234` (a laptop, CPU). It differs from the llama.cpp README in places, so the client follows what the server does:
  - `GET /v1/models` returns `{"models":[{"name":"basal-2.0-mini","release_date":…,"description":…,"mode":"eager","temperature_per_prim":{"choice":…,"noul":…,"score":…},…}]}` – no `data[].id` and no `architecture.output_modalities` (llama.cpp's shape). The probe has to accept the server's shape (`models[].name`), and llama.cpp's (`data[].id` with `"decisions"`) if the client is to work with both.
  - `GET /health` → 200 `{"status":"ok"}` (`/v1/health` is 404).
  - `POST /v1/systemone`: every answer carries its `type`. `choice` → `choice`, `probabilities` per option, `confidence` = the chosen option's probability (not the normalized (p − 1/n)/(1 − 1/n) of the README example). `score` → `score` (expected level, fractional), `legend` `{"0":…}`, `probabilities` keyed `"0"`…`"3"`, `confidence` = the most likely level's probability. `noul` → `noul`, `probabilities` `{"true","false"}`, `confidence`. `usage` adds `questions`, `branches`, `latency_ms`, `longest_prompt_tokens`; the request's `model` field is ignored.
  - Invalid requests → **422** `{"error":"…"}` (fewer than 2 options, unknown type, no questions, malformed JSON), answered in milliseconds; GET → 405; unknown paths → 404.
  - Latency: 76–90 s for the four questions (about 20 s per question; ~600–700 input tokens). `Model:SystemOne:Timeout` must be several minutes, and `Host:ShutdownTimeout` (20 s) will cancel an evaluation in flight at shutdown – the message is then redelivered, which is correct but wastes the work.
  - A 10 220-character comment (the current `Consumer:MaxContentLength` is 10 000) got no answer within 20 minutes, while the server kept answering `/health` and `/v1/models` at once. Long comments are therefore the main risk: with a 5-minute timeout such a comment takes its counted retries (the probe still lists the model) and ends in the DLQ, but occupies the server for 4 × 5 minutes on the way. `MaxContentLength` must be lowered to what the server answers in time (done for the local setup in S3). Worse, if the server keeps computing a request after the client gave up (S3: it does), the abandoned work delays the comments behind it: a normal comment can then time out too while the model is listed, and be dead-lettered after its retries (S2 review).
  - Answers: an insult with an ad link and a phone number → reject 0.99, toxicity 1.79/3, spam 0.94, personal data 0.95. A polite disagreement → approve 0.96, toxicity 0.74/3 (0.25 – close to the 0.35 limit), spam 0.09, personal data 0.03.

**S1. Signals and review reasons in the domain** `[x]`
- Core: `ModerationSignals` (toxicity, spam, personal data in [0, 1]) as an optional part of `ModelPrediction`; `DecisionPolicyOptions` limits `MaxToxicityForApprove` (0.35), `MaxSpamForApprove` and `MaxPersonalDataForApprove` (0.5), validated to [0, 1]; `DecisionPolicy.Decide` returns a `PolicyDecision` (decision + `ReviewReason`s), carried by `AnalysisResult.ReviewReasons`. A model without signals (Static, ONNX) behaves as before.
- Persistence: migration `AddReviewReasonsAndSignals` – `review_reasons text[] NOT NULL DEFAULT '{}'` (enum names) and nullable `toxicity`, `spam_probability`, `personal_data_probability` with [0, 1] check constraints. Plain columns rather than one jsonb, so they can be queried and constrained.
- `CommentAnalyzed` is unchanged (open point: whether it should carry the review reasons).
- After the S1 review: the migration fills `review_reasons = {LowConfidence}` for existing ManualReview rows (the only way to manual review before S1), and a check constraint `ck_comment_analyses_review_reasons` enforces "reasons if and only if ManualReview" (verified on the compose database in a rolled-back transaction). `ReviewReason` names are stored, so the members must not be renamed. The new limits are listed in `appsettings.json`.

**S2. `SystemOneDecisionModel`** `[x]`
- Replaces `LlmDecisionModel` (`Model:Provider=SystemOne`, options `Model:SystemOne`: `BaseUrl`, `ApiKey`, `Model`, `Version`, `Timeout` – default 5 min, at most 10). The questions are constants in `SystemOneQuestions`; the version hashes the framing (`plain-state-v1`, the comment as the plain `state` string) and the serialized questions.
- Validation (`SystemOneResponseException`, counted retries then the DLQ): a missing answer, an answer `type` other than the one asked (the type itself is optional), a verdict choice other than `approve`/`reject`, verdict probabilities that are missing, have other keys, are outside [0, 1], do not sum to 1 ± 0.001 or do not make the choice the most likely option, a toxicity `score` outside [0, 3], and a `noul` outside [0, 1]. The score's level probabilities are not checked: only the expected score is used.
- Unavailable model (as in F2): an unreachable server, a response without `model` or with another `model` (e.g. LM Studio's 200 "Unexpected endpoint" on the same port), and timeouts or 3xx/401/403/404/408/429/5xx (including 501) that the probe confirms. The probe (`GET models`, also readiness) accepts the Basal list (`models[].name`) and llama.cpp's (`data[].id` with `"decisions"` in `architecture.output_modalities`), so an OpenAI-style list such as LM Studio's does not count. 4xx such as 400/422 are failures of the comment.
- Live check through the class against the Basal server: readiness Healthy, version `basal-2.0-mini@2.0+cef8109c93be`; a polite comment → Approve 0.857 (toxicity 0.19, spam 0.06, personal data 0.04), an injection attempt with an insult → Reject 0.983 (toxicity 0.81, spam 0.93). Each took about 122 s instead of 76–90 s, possibly because the server was still busy with the abandoned long request (see S0).
- After the S2 review: the request also names the `model` (ignored by the Basal server, needed by servers with several models), and a response is limited to 1 MB. Not verified yet: whether TypeSafe's cloud has a model list in one of the accepted shapes (phase G).
- Development settings and compose switched to `SystemOne` in this step (the `Llm` provider no longer exists, and the tests load the Development settings); `CLAUDE.md` updated.

**S3. End-to-end in compose** `[x]`
- The server does **not** stop working on a request when the client disconnects: a single-question request took 8.7 s on an idle server, but 82 s right after a four-question request was abandoned after 5 s. Requests are served one at a time.
- Latency of the four questions grows with the comment: about 80–120 s for short comments, 202 s for 1000 characters (1352 input tokens), 364 s for 2500 characters (beyond the 5-minute timeout), and no answer within 20 minutes for 10 220. Development settings and compose therefore set `Consumer:MaxContentLength` to 1000 (about 1.5× below the timeout); the shipped default (10 000) is left alone, because the limit depends on the hardware. Longer comments go straight to the DLQ (verified with 1001 characters) – whether they should rather go to manual review is a product decision (S4). The limit counts characters and was measured on Polish prose; input that tokenizes badly (digits, random strings, emoji) can still exceed the timeout, so S4's samples include such input.
- After the S3 review: Development and compose also set `Consumer:Prefetch` to 1. A prefetched message waits unacked, and `Prefetch × (Model:SystemOne:Timeout + the 30 s maximum retry delay)` must stay well below RabbitMQ's `consumer_timeout` (30 min): otherwise the broker closes the channel, and the redeliveries count towards `x-delivery-limit`. The limit counts characters and was measured on Polish prose; input that tokenizes badly (digits, random strings, emoji) can still exceed the timeout, so S4's samples include such input.
- After the S3 review: Development and compose also set `Consumer:Prefetch` to 1. A prefetched message waits unacked, and `Prefetch × (Model:SystemOne:Timeout + the 30 s maximum retry delay)` must stay well below RabbitMQ's `consumer_timeout` (30 min): otherwise the broker closes the channel, and the redeliveries count towards `x-delivery-limit`.
- Compose against the Basal server on the host (it listens on `0.0.0.0:1234`): readiness Healthy from the container, the migration applied, and four Polish comments processed in 82–101 s each and their events published:

  | comment | decision | reasons | confidence | toxicity | spam | personal data |
  |---|---|---|---|---|---|---|
  | polite praise | Approve | – | 0.870 | 0.19 | 0.07 | 0.03 |
  | insult aimed at the author | Reject | – | 0.924 | 0.60 | 0.28 | 0.02 |
  | loan advert with a link | Reject | – | 0.970 | 0.35 | 0.96 | 0.03 |
  | agreement plus a phone number and an address | ManualReview | LowConfidence, Spam, PersonalData | 0.732 | 0.27 | 0.54 | 0.97 |

- Not checked live: stopping the model server (it is the user's process); unit tests cover the unavailable model, and F2 verified the consumer side. Observed: the worker logs `libgssapi_krb5.so.2: cannot open shared object file` once at startup (the runtime image lacks the Kerberos library, which only Negotiate authentication would use); harmless, to be silenced in H4 if wanted.

**S4. Polish quality check** `[ ]` (replaces F3)
- Labelled sample in `tests/data/` (criticism, swearing, threats, spam, personal data, injection, long comments), an opt-in report (accuracy, decisions, review reasons, latency), and the long-comment behaviour. **Decision point for the user:** question texts, thresholds and limits.

### Phase G – Jev alternative

**G1. Jev client**
- `JevOptions` (BaseUrl, ApiKey from an env var, a pinned Model), request and response DTOs that follow the documented contract, and a typed `HttpClient` with a standard resilience handler (retries on 429/529 that honour `Retry-After`).
- Unit tests with a stubbed `HttpMessageHandler`.

**G2. JevDecisionModel**
- Sends a `choice` question (`approve` / `reject`) with the moderation instructions taken from config, and maps the response `choice`, `confidence` and `model` to a `ModelPrediction`. Enabled with `Model:Provider=Jev`.
- `IDecisionModel.Info.Version` stays the version pinned in `Jev:Model`, so the idempotency check before calling the model and the stored version always agree. `JevDecisionModel` compares the response `model` with the pinned version and treats a mismatch as an error (no silent storage under the wrong version). The mismatch is a non-transient error: the message goes straight to the DLQ (no retries), it is logged at Error level, and readiness reports it, because every following message would fail the same way.
- Tests: mapping, and how errors are handled.

### Phase H – Production readiness

**H1. Observability**
- Metrics through `System.Diagnostics.Metrics`: decisions by type, inference duration, processing failures, outbox lag (age of the oldest unpublished row) and retries. Optional OpenTelemetry export (OTLP) behind config.

**H2. Graceful shutdown and scaling**
- Shutdown behaviour itself is implemented in D2; here verify it under load: `docker compose stop` during processing, and run compose with 2 worker replicas and confirm there are no duplicates and no lost events.

**H3. Documentation**
- README: architecture, configuration reference, running locally, sending a test comment, swapping models, and the production migration bundle.

**H4. Production hardening**
- Enforce TLS outside Development: reject `amqp://` (require `amqps://`) and PostgreSQL connection strings without `SSL Mode=VerifyFull`.
- Pin container image tags to a patch version or digest (Dockerfile, compose, Testcontainers).
- Document the RabbitMQ policies to use instead of queue arguments for tuning existing queues (delivery limit, dead-letter strategy). Note for operators: `x-overflow: reject-publish` only matters once a max-length is set – then publishers get nacks and must use publisher confirms. The DLQ is a quorum queue with RabbitMQ 4's implicit delivery limit (20) and no dead-letter target, so a message that crashes 20 DLQ consumers in a row would be dropped; set an explicit limit via policy if DLQ replay tooling is introduced.
- After a broker-side consumer cancel (e.g. the input queue was deleted), the D2 recreate loop retries `BasicConsume` until the queue exists again, and only readiness reports the problem. Decide whether the loop should re-declare the topology, or whether queue deletion stays an operator error (D2 review).

### Phase I – Optional local ONNX classifier

Deferred: the default model is the `/v1/systemone` decision model (phase S). Kept for a non-generative alternative (faster, calibrated probabilities).

**I1. Model export script**
- `scripts/export-model.sh` and `.ps1`: run Python `optimum` in a container to export the HF model (default `citizenlab/distilbert-base-multilingual-cased-toxicity`) to `models/<name>/model.onnx` + `vocab.txt` + `config.json`. `models/` is git-ignored.
- Done when: the script produces the files.

**I2. OnnxToxicityModel**
- Microsoft.ML.OnnxRuntime + Microsoft.ML.Tokenizers (`BertTokenizer`): truncate to the maximum length, run inference, apply softmax, and map the label from `config.json` (`id2label`). The model version is computed from the file hash at load time. The `InferenceSession` is a singleton.
- Tests run against the real model and are skipped when the model files are missing. They cover an obviously toxic and an obviously neutral comment in English and Polish.

**I3. Wire ONNX into the host**
- `Model:Provider=Onnx` can be selected in compose, and the model directory is mounted as a read-only volume. Readiness also reports that the model is loaded.
- Manual end-to-end check (see Verification).

**I4. Polish quality check**
- A small labelled sample of Polish comments in `tests/data/`, plus a test or console report showing the decision distribution and accuracy at the default thresholds.
- Outcome: either keep the model and tune the thresholds, or pick a different model. **Decision point for the user.**

---

## Verification (end-to-end, after S3)
1. Start the `/v1/systemone` server with `basal-2.0-mini` on port 1234, listening beyond localhost (the container reaches it through `host.docker.internal`). On the author's laptop the best configuration is `basal-serve` with the Q8_0 GGUF build – the exact command is in the README ("Decision model").
2. `docker compose up --build`
3. RabbitMQ UI at http://localhost:15672: publish a `CommentCreated` JSON message to the `comments` exchange with routing key `comment.created`.
4. `SELECT decision, review_reasons, confidence, toxicity, spam_probability, personal_data_probability, model_version FROM comment_analyses;` shows the decision with its reasons, the signals and the model version.
5. A queue bound to `comment.analyzed` receives the event.
6. `curl http://localhost:7461/health/ready` returns Healthy. A malformed message, or a comment over 1000 characters, lands in the DLQ.

## Open points
- Quality of the decision model on Polish comments, and the thresholds and signal limits (S4).
- Prompt injection: a comment can try to steer the model. It can only shift the probabilities of the fixed options, so at worst a comment gets the wrong decision; S4 includes such samples.
- Throughput: about 80–120 s per short comment and about 200 s at the 1000-character limit (four questions) on one laptop CPU; the server works through requests one at a time and does not stop on a client disconnect.
- Whether the comment service should consume `CommentAnalyzed`, and whether the event should carry the review reasons.
