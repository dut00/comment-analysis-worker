# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A .NET 10 worker that consumes `CommentCreated` messages from RabbitMQ and evaluates each comment with a decision model. The outcome is **Approve**, **Reject** or **ManualReview**. The worker stores the decision, the model **confidence** and the **model version** in PostgreSQL, then publishes `CommentAnalyzed`. It is stateless and runs in containers.

The project is being built incrementally. `docs/implementation-plan.md` is the source of truth for the target design and for what is done (`[x]`), in progress (`[~]`) or todo (`[ ]`). Read it before starting work, because much of the architecture below is not implemented yet.

## Working agreements

- Talk to the user in Polish. Write all code, comments, commit messages and documentation (including this file and the plan) in English.
- Work one increment from the plan at a time. Each increment ends with a passing build and tests, the status marked in `docs/implementation-plan.md`, and **one commit**. Then stop and report before starting the next increment.
- The default branch is `master`.

## Commands

```bash
dotnet build                                   # warnings are errors (TreatWarningsAsErrors)
dotnet test                                    # all tests
dotnet test tests/CommentAnalysis.Core.Tests   # one project
dotnet test --filter "FullyQualifiedName~DecisionPolicyTests"            # one class
dotnet test --filter "FullyQualifiedName=Namespace.Class.Method"         # one test
dotnet format --verify-no-changes              # check style against .editorconfig
```

Integration tests (`tests/CommentAnalysis.IntegrationTests`) will use Testcontainers, so Docker must be running.

## Build conventions

- Shared settings live in `Directory.Build.props` (net10.0, nullable, implicit usings, `AnalysisLevel=latest-recommended`, code style enforced at build). Project files stay minimal and should not repeat these settings.
- Central Package Management is on. Add a `<PackageVersion>` to `Directory.Packages.props`, and put only a `<PackageReference Include="..." />` with no version in the `.csproj`.
- `tests/Directory.Build.props` imports the root props and adds the xUnit packages and `using Xunit` to every test project automatically.
- Style rules from `.editorconfig` are enforced at build time: file-scoped namespaces, braces required, private fields named `_camelCase`, LF line endings. Underscores are allowed in test method names.

## Architecture (target – see plan for current status)

Dependency direction: `Worker → Infrastructure → Core`. Core has no infrastructure dependencies.

- **Core**: domain and contracts. `IDecisionModel` is the abstraction over the model. `DecisionPolicy` turns a `ModelPrediction` (label + confidence) into a `Decision` using configurable thresholds. Approve requires `non-toxic` with confidence ≥ `ApproveThreshold`, Reject requires `toxic` with confidence ≥ `RejectThreshold`, and everything else is ManualReview. Message records `CommentCreated` and `CommentAnalyzed` are serialized with camelCase JSON and enums as strings.
- **Infrastructure**: implementations of `IDecisionModel`, selected by `Model:Provider`. The default is a local ONNX toxicity classifier (`OnnxToxicityModel`). An alternative is the Jev decision model by TypeSafe AI (`POST https://api.typesafe.ai/v1/systemone`, `choice` question, version taken from the response `model` field). Also contains EF Core/Npgsql persistence and RabbitMQ.Client 7.x (async API, used directly without MassTransit).
- **Worker**: `Microsoft.NET.Sdk.Web` host that exposes only health and metrics endpoints. It contains `CommentCreatedConsumer` and `OutboxPublisher`.

Cross-cutting design points that span several files:
- **Model version** is persisted with every analysis. For ONNX it is `{Name}@{Version}+{first 12 hex chars of the model file's SHA-256}`, so swapping the file always produces a new version.
- **Idempotency**: `comment_analyses` has `UNIQUE(comment_id, model_version)`. The consumer skips comments already analyzed by the current model version, and acks only after the DB commit.
- **Transactional outbox**: the analysis row and the `CommentAnalyzed` outbox row are written in one transaction. `OutboxPublisher` publishes with publisher confirms and claims rows with `FOR UPDATE SKIP LOCKED`, so multiple replicas are safe.
- **Failures**: malformed messages go straight to the DLQ. Transient errors are retried via an `x-retry-count` header up to `Consumer:MaxRetries`, then sent to the DLQ.
- **RabbitMQ topology**: topic exchange `comments`. Input queue `comment-analysis.comment-created` (routing key `comment.created`) with a DLQ. Output routing key `comment.analyzed`.
