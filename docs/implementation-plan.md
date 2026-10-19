# Comment Analysis Worker – Implementation Plan

## Goal
A .NET 10 worker that consumes newly created user comments from RabbitMQ, evaluates each one with a decision model, and decides **Approve**, **Reject** or **ManualReview**. For every comment it stores the decision, the model's **confidence** (score) and the **model version** in PostgreSQL. It then publishes a `CommentAnalyzed` event. The worker is stateless and runs in containers.

The model is hidden behind `IDecisionModel`:
- **Default:** a local ONNX toxicity classifier, which needs no API key and makes no external calls.
- **Alternative:** the Jev decision model by TypeSafe AI (`POST https://api.typesafe.ai/v1/systemone`), which can be switched on in configuration once an API key is available.

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
- The model returns a `ContentLabel` (`Acceptable` / `Unacceptable`) and a confidence for it. ONNX maps `non-toxic` → Acceptable and `toxic` → Unacceptable with `confidence = max(pToxic, 1 - pToxic)`; Jev maps `approve` → Acceptable and `reject` → Unacceptable.
- `Acceptable` and `confidence >= ApproveThreshold` (default 0.85) → Approve
- `Unacceptable` and `confidence >= RejectThreshold` (default 0.90) → Reject
- anything else → ManualReview

### Model version
`{Name}@{Version}+{first 12 hex chars of SHA-256 of the model file}` for ONNX. For Jev, the `model` field returned by the API (e.g. `jev-1.13.0`).

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

**A3. Decision policy**
- `DecisionPolicyOptions` (thresholds, validated to [0.5, 1]) and `DecisionPolicy.Decide(ModelPrediction)`.
- Unit tests: each outcome, exact boundary values, and invalid options.
- Done when: all policy tests pass.

**A4. Model abstraction and analyzer**
- `IDecisionModel` (`ModelInfo Info`, `Task<ModelPrediction> EvaluateAsync(string content, CancellationToken)`).
- `CommentAnalyzer`, which calls the model and the policy and returns an `AnalysisResult` (decision, confidence, model info, duration).
- A `FakeDecisionModel` in the test project.
- Done when: the analyzer unit tests pass.

**A5. Message contracts**
- `CommentCreated` and `CommentAnalyzed` records, plus shared `JsonSerializerOptions` (camelCase, enums as strings).
- Tests: serialization round-trip and the shape of the JSON.

### Phase B – Runnable host

**B1. Minimal worker host**
- `Program.cs`: options binding with `ValidateOnStart`, structured console logging, a `/health/live` endpoint, and a `Model:Provider` setting. A `Static` provider returns a fixed prediction, so the worker runs without a real model.
- Done when: `dotnet run` starts and `/health/live` returns 200.

**B2. Container**
- Multi-stage `Dockerfile` (sdk:10.0 → aspnet:10.0, non-root user, port 8080) and a `docker-compose.yml` containing only the worker.
- Done when: `docker compose up --build` starts and the health check passes.

### Phase C – Persistence (PostgreSQL)

**C1. DbContext and initial migration**
- EF Core 10 + Npgsql, `AnalysisDbContext`, and the `comment_analyses` table with `UNIQUE(comment_id, model_version)`.
- First migration, applied at startup when `Database:MigrateOnStartup=true`. Postgres is added to compose.
- Done when: the worker in compose creates the table.

**C2. Analysis store and idempotency**
- `IAnalysisStore` with `ExistsAsync(commentId, modelVersion)` and `SaveAsync(result)`, implemented with EF.
- Integration tests on a Postgres Testcontainer: save, then detect the duplicate. Also check that the unique constraint holds under a race.

**C3. Readiness check for Postgres**
- `/health/ready` reports Postgres status.

### Phase D – Messaging (RabbitMQ)

**D1. Connection and topology**
- `RabbitMqOptions`, a singleton connection provider (RabbitMQ.Client 7.x, async API), and a `TopologyInitializer` that declares the exchange, the queue, the DLX and the DLQ. RabbitMQ is added to compose, and readiness now also checks RabbitMQ.
- Done when: the queues are visible in the management UI after startup.

**D2. Consumer – happy path**
- `CommentCreatedConsumer` as a `BackgroundService`: manual ack, configurable prefetch, a new DI scope per message, and a log scope carrying `commentId`. It deserializes the message, skips it if already analyzed, analyzes it, saves the result and then acks.
- Integration test (Postgres + RabbitMQ containers, fake model): publishing a message produces a DB row.

**D3. Failure handling**
- Malformed JSON or failed validation → straight to the DLQ.
- Transient errors → republish with an `x-retry-count` header up to `Consumer:MaxRetries`, then DLQ.
- Integration tests for each path.

### Phase E – Outgoing events

**E1. Transactional outbox – write side**
- `outbox_messages` table and migration. `CommentAnalyzed` is written in the same transaction as the analysis row.
- Test: both rows exist after processing, or neither does.

**E2. Outbox publisher**
- A `BackgroundService` that polls unpublished rows (`FOR UPDATE SKIP LOCKED`, so multiple replicas are safe), publishes with publisher confirms and marks the rows as published.
- End-to-end integration test: `CommentCreated` in → `CommentAnalyzed` out.

### Phase F – Real model (ONNX)

**F1. Model export script**
- `scripts/export-model.sh` and `.ps1`: run Python `optimum` in a container to export the HF model (default `citizenlab/distilbert-base-multilingual-cased-toxicity`) to `models/<name>/model.onnx` + `vocab.txt` + `config.json`. `models/` is git-ignored.
- Done when: the script produces the files.

**F2. OnnxToxicityModel**
- Microsoft.ML.OnnxRuntime + Microsoft.ML.Tokenizers (`BertTokenizer`): truncate to the maximum length, run inference, apply softmax, and map the label from `config.json` (`id2label`). The model version is computed from the file hash at load time. The `InferenceSession` is a singleton.
- Tests run against the real model and are skipped when the model files are missing. They cover an obviously toxic and an obviously neutral comment in English and Polish.

**F3. Wire ONNX into the host**
- `Model:Provider=Onnx` becomes the default in compose, and the model directory is mounted as a read-only volume. Readiness also reports that the model is loaded.
- Manual end-to-end check (see Verification).

**F4. Polish quality check**
- A small labelled sample of Polish comments in `tests/data/`, plus a test or console report showing the decision distribution and accuracy at the default thresholds.
- Outcome: either keep the model and tune the thresholds, or pick a different model. **Decision point for the user.**

### Phase G – Jev alternative

**G1. Jev client**
- `JevOptions` (BaseUrl, ApiKey from an env var, a pinned Model), request and response DTOs that follow the documented contract, and a typed `HttpClient` with a standard resilience handler (retries on 429/529 that honour `Retry-After`).
- Unit tests with a stubbed `HttpMessageHandler`.

**G2. JevDecisionModel**
- Sends a `choice` question (`approve` / `reject`) with the moderation instructions taken from config, and maps the response `choice`, `confidence` and `model` to a `ModelPrediction`. Enabled with `Model:Provider=Jev`.
- Tests: mapping, and how errors are handled.

### Phase H – Production readiness

**H1. Observability**
- Metrics through `System.Diagnostics.Metrics`: decisions by type, inference duration, processing failures. Optional OpenTelemetry export (OTLP) behind config.

**H2. Graceful shutdown and scaling**
- Stop consuming, finish the in-flight messages, then close the channel. Run compose with 2 worker replicas and confirm there are no duplicates and no lost events.

**H3. Documentation**
- README: architecture, configuration reference, running locally, sending a test comment, swapping models, and the production migration bundle.

---

## Verification (end-to-end, after F3)
1. `scripts/export-model.sh`
2. `docker compose up --build`
3. RabbitMQ UI at http://localhost:15672: publish a `CommentCreated` JSON message to the `comments` exchange with routing key `comment.created`.
4. `SELECT * FROM comment_analyses;` shows the decision, confidence and model_version.
5. A queue bound to `comment.analyzed` receives the event.
6. `curl http://localhost:8080/health/ready` returns Healthy. A malformed message lands in the DLQ.

## Open points
- Quality of the ONNX model on Polish comments (F4).
- Threshold values (0.85 / 0.90 are starting points).
- Whether the comment service should consume `CommentAnalyzed`, and what payload it needs beyond the current one.
