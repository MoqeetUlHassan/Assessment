# Assessment

ASP.NET Core (.NET 10) Web API on PostgreSQL.

Backend for facilities maintenance requests: multi-tenant organisations, sites, permission-based roles, threshold-based approval with re-approval on changes, a spend report, and an immutable audit trail.

| Doc | What's in it |
|---|---|
| [PLAN.md](PLAN.md) | Agreed design: data model, permissions, approval rules, transition table, API, tests |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Layers, request flow, where each guarantee is enforced (diagrams) |
| [SCHEMA.md](SCHEMA.md) | ER diagram, composite FKs, constraints, indexes and the query each serves |
| [DECISIONS.md](DECISIONS.md) | Technical choices, what was rejected, trade-offs, assumptions |
| [AI-LOG.md](AI-LOG.md) | How the AI agent was used and steered |
| [CLAUDE.md](CLAUDE.md) | Agent configuration and rules |

> Implementation in progress. Run instructions below cover the current state.

## Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 10.0.x (pinned in `global.json`, later 10.0 feature bands accepted) | `dotnet --list-sdks` |
| PostgreSQL | 14+ — **either** Docker **or** a local install | `docker --version` / `psql --version` |

## Run it (about 5 minutes on a clean machine)

```bash
git clone <repo-url> && cd Assessment

# 1. Start Postgres (skip if you already have one on localhost:5432 with user/password postgres/postgres)
docker compose up -d db

# 2. Run the API. It creates the database and applies migrations on startup.
dotnet run --project src/Assessment.Api --launch-profile http
```

Check it's up:

```bash
curl http://localhost:5183/health     # -> Healthy
```

OpenAPI document (Development only): http://localhost:5183/openapi/v1.json

### Using your own Postgres instead of Docker

The default connection string (in `src/Assessment.Api/appsettings.Development.json`) is:

```
Host=localhost;Port=5432;Database=assessment;Username=postgres;Password=postgres
```

If your credentials differ, override the connection string without editing files. Either:

```bash
# user-secrets (persisted per machine, outside the repo)
dotnet user-secrets --project src/Assessment.Api init
dotnet user-secrets --project src/Assessment.Api set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=assessment;Username=me;Password=secret"

# or an environment variable, for the current shell only
export ConnectionStrings__Default="Host=...;Username=me;Password=secret"     # bash
$env:ConnectionStrings__Default="Host=...;Username=me;Password=secret"       # PowerShell
```

Port 5432 already taken by a local Postgres but you still want the Docker one? Run `POSTGRES_PORT=5433 docker compose up -d db`, then override the connection string with `Port=5433`.

## Run the tests

The tests boot the real app against a **real Postgres** database named `assessment_test`, which is created automatically. Postgres must be running (step 1 above).

```bash
dotnet test
```

To point the tests at a different server, set `TEST_CONNECTION_STRING`.

## Database migrations

`dotnet-ef` is pinned as a local tool:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Assessment.Api
```

In Development, migrations are applied automatically on startup. In any other environment they are not (see DECISIONS.md).

## Project layout

```
src/Assessment.Api/          Web API (minimal APIs, EF Core, Npgsql)
  Data/AppDbContext.cs       EF Core context; entity configs picked up by assembly scan
tests/Assessment.Api.Tests/  xUnit integration tests via WebApplicationFactory
docker-compose.yml           Postgres 16 for local dev / reviewers
```

## Troubleshooting

- **`fail: ... An error occurred using the connection to database 'assessment'` on first start.** This is expected: EF Core checks whether the database exists before creating it. If `/health` returns `Healthy`, nothing is wrong.
- **`Connection string 'ConnectionStrings:Default' is missing`**: you are running outside the Development environment. Use `--launch-profile http`, or set the connection string as shown above.
- **`password authentication failed for user "postgres"`**: your local Postgres uses different credentials. Override them as shown above.
