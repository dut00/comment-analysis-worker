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

The configuration that works best on the author's laptop (CPU) runs the Q8_0 GGUF build with `basal-serve`:

```powershell
basal-serve --model "C:\Users\Kuba\.lmstudio\models\Remek\basal-2.0-mini-GGUF" --mode gguf --gguf "C:\Users\Kuba\.lmstudio\models\Remek\basal-2.0-mini-GGUF\basal-2.0-mini-Q8_0.gguf" --name basal-2.0-mini --port 1234
```

- `--name basal-2.0-mini` matters: the server reports this name in every response, and the worker treats any other name as another model (the comments wait). Without it, `basal-serve` on Windows reports the whole `--model` path.
- The server listens on all interfaces (`0.0.0.0:1234`), so the worker in compose can reach it.
- Check it with `curl http://localhost:1234/v1/models` (lists `basal-2.0-mini`) and `curl http://localhost:1234/health`.
- Expect about 11 s per short comment (four questions), about 19 s for 2500 characters and about 52 s for 10 000 (the eager mode without `--mode gguf` was 7–20 times slower). The server handles one request at a time and finishes a request even after the client gave up, so Development and compose prefetch one message. Comments over `Consumer:MaxContentLength` (1000 characters; comments are expected to have at most 280) go to manual review without the model.
- Without the model, set `Model__Provider: Static` in compose (or `Model:Provider` in Development settings).
