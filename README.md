# Comment Analysis Worker

A .NET 10 worker that consumes newly created user comments from RabbitMQ and evaluates them with a decision model. Each comment is approved, rejected or sent to manual review. The worker stores the decision, the model confidence and the model version in PostgreSQL.

> Work in progress. See [docs/implementation-plan.md](docs/implementation-plan.md) for the plan and current status.

## Requirements

- .NET SDK 10.0
- Docker (for containers and integration tests)

## Build and test

```bash
dotnet build
dotnet test
```

## Decision model

The worker queries a decision model through the TypeSafe-compatible `POST /v1/systemone` API (`Model:Provider=SystemOne`). Development settings and compose expect `basal-2.0-mini` on port 1234 of the host (compose reaches it through `host.docker.internal`).

The configuration that works best on the author's laptop (CPU) runs the Q8_0 GGUF build with `basal-serve` (installation and the model files: [docs/basal-windows.md](docs/basal-windows.md)):

```powershell
basal-serve --model "C:\Users\Kuba\.lmstudio\models\Remek\basal-2.0-mini-GGUF" --mode gguf --gguf "C:\Users\Kuba\.lmstudio\models\Remek\basal-2.0-mini-GGUF\basal-2.0-mini-Q8_0.gguf" --name basal-2.0-mini --port 1234
```

- `--name basal-2.0-mini` matters: the server reports this name in every response, and the worker treats any other name as another model (the comments wait). Without it, `basal-serve` on Windows reports the whole `--model` path.
- The server listens on all interfaces (`0.0.0.0:1234`), so the worker in compose can reach it.
- Check it with `curl http://localhost:1234/v1/models` (lists `basal-2.0-mini`) and `curl http://localhost:1234/health`.
- Expect about 11 s per short comment (four questions), about 19 s for 2500 characters and about 52 s for 10 000 (the eager mode without `--mode gguf` was 7–20 times slower). The server handles one request at a time and finishes a request even after the client gave up, so Development and compose prefetch one message. Comments over `Consumer:MaxContentLength` (1000 characters; comments are expected to have at most 280) go to manual review without the model.
- Without the model, set `Model__Provider: Static` in compose (or `Model:Provider` in Development settings).

### Switching to TypeSafe's Jev through OpenRouter

The same client queries TypeSafe's cloud model Jev through OpenRouter (checked live, phase G2). It is more accurate and much faster than the local model on the labelled sample ([side-by-side comparison](docs/quality/comparison.md)), but it runs in the cloud: comment content leaves the machine, and each comment costs about $0.00002.

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

Back to the local Basal server: `docker compose up --build -d` (compose without the override), or `dotnet run --project src/CommentAnalysis.Worker` (the default `http` profile) in a new shell or after `Remove-Item Env:Model__SystemOne__ApiKey`. Analyses keep the model version they were made with, so both models' results stay apart in `comment_analyses`; a comment already analyzed by one model is analyzed again by the other only if its message is delivered again.

What to expect:

- A comment takes about 0.3–0.9 s.
- The model is pinned to the dated snapshot the responses name (`typesafe/jev-1.14-20261014`); `typesafe/jev-1.14` would never match. A new snapshot (e.g. `jev-1.15-…`) changes the stored model version.
- Readiness needs no key, so a wrong key (401) or an empty account (402) is noticed by the first comment: comments then wait without using up their retries, and readiness turns unhealthy. The same happens when OpenRouter's provider is down (502/503).
- A rate limit (429) or an overload (529) also makes comments wait; the worker honours `Retry-After` up to 30 s. An overload makes readiness unhealthy for a minute; a rate limit does not.
- TypeSafe's own API (`https://api.typesafe.ai/v1/`, model e.g. `jev-1.14.0`) uses the same contract but has not been checked.

### Comparing models

`scripts/quality-report.cs` evaluates the labelled comments in `tests/data/polish-comments.json` with one model and writes its report; `scripts/compare-models.cs` puts saved results side by side without querying any model:

```bash
# Jev (the key from the environment, never an argument); the results file lets a rerun resume
SYSTEMONE_API_KEY=… dotnet run scripts/quality-report.cs -- tests/data/polish-comments.json docs/quality/jev-1.14-results.json docs/quality/jev-1.14-report.md https://openrouter.ai/api/v1/ typesafe/jev-1.14-20261014 1.14-20261014
# Basal (defaults: http://localhost:1234/v1/, basal-2.0-mini, 2.0)
dotnet run scripts/quality-report.cs -- tests/data/polish-comments.json docs/quality/basal-2.0-mini-results.json docs/quality/basal-2.0-mini-report.md
# Side by side, at the current default policy
dotnet run scripts/compare-models.cs -- tests/data/polish-comments.json docs/quality/comparison.md basal-2.0-mini=docs/quality/basal-2.0-mini-results.json jev-1.14=docs/quality/jev-1.14-results.json
```

## Metrics

The worker records metrics with `System.Diagnostics.Metrics` (meter `CommentAnalysis.Worker`: decisions, review reasons, model duration, retries, requeues, dead-letters, published events and the outbox lag) and exports them over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` (or `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`) is set; without it nothing is exported. The other standard settings apply (`OTEL_EXPORTER_OTLP_PROTOCOL` – gRPC by default, `OTEL_EXPORTER_OTLP_HEADERS` for a collector's key, from a secret store – `OTEL_METRIC_EXPORT_INTERVAL`, `OTEL_SERVICE_NAME`, default `comment-analysis-worker`). An endpoint that is not an absolute http(s) URI stops the start, but a reachable-looking wrong one does not: export errors are not in the worker's logs (only in the OpenTelemetry SDK's self-diagnostics).

To see the metrics locally in the Aspire Dashboard (compose passes `WORKER_OTLP_ENDPOINT` on, so a shell's own `OTEL_EXPORTER_OTLP_ENDPOINT` does not leak into the container):

```bash
WORKER_OTLP_ENDPOINT=http://aspire-dashboard:18889 docker compose --profile observability up -d
```

Then open http://localhost:18888 → Metrics → `comment-analysis-worker`. Alert on `comment_analysis.outbox.oldest_pending_age` with max across replicas, and treat a missing value as a problem too (the worker reports none while it cannot read the outbox).
