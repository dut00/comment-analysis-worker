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
