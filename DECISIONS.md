# Decisions

Significant technical choices, what was rejected, and the trade-off. Updated as the project evolves.

## Platform & structure

| Decision | Rejected | Trade-off |
|---|---|---|
| **.NET 10 (LTS)**, SDK pinned in `global.json` with `rollForward: latestFeature` | .NET 8 (older LTS), .NET 9 (STS) | Newest LTS, with support into 2028. A reviewer needs a 10.0 SDK; the pin accepts any later 10.0 feature band, so an exact patch match isn't required. |
| **Single API project + one test project** | Clean Architecture split (Domain / Application / Infrastructure / Api) | Fewer files and less ceremony for a small scope. The layers live in folders instead of assemblies. I'll split into projects only if a boundary actually needs enforcing. |
| **Minimal APIs** | MVC controllers | Less boilerplate and first-class in .NET 10 (validation, OpenAPI). Controllers only pay off with heavy filter/convention use, which isn't expected here. |

## Data access

| Decision | Rejected | Trade-off |
|---|---|---|
| **PostgreSQL** | SQLite, SQL Server | SQLite needs no setup, but it differs from production in concurrency, types and migrations, and reviewers may see it as a shortcut. SQL Server is heavier to run locally and on macOS/ARM. Postgres costs a setup step, which `docker compose` mitigates. |
| **EF Core 10 + Npgsql** | Dapper, raw ADO.NET | Migrations, change tracking and LINQ out of the box. It costs some control over SQL; Dapper remains an option for specific hot queries. |
| **Migrate on startup, Development only** (`Database:MigrateOnStartup`) | Always migrate on startup; manual `dotnet ef database update` | One-command start for reviewers (the 15-minute budget). It's off by default elsewhere, because migrating on startup is unsafe with multiple instances or restricted DB permissions. |
| **Connection string failure is fatal at startup** with a README pointer | Lazy failure on first query | A misconfigured clean machine gets an actionable error in the first second, not a stack trace on the first request. |

## Testing

| Decision | Rejected | Trade-off |
|---|---|---|
| **Integration tests against real Postgres** via `WebApplicationFactory` | EF InMemory provider, SQLite-in-memory | InMemory doesn't enforce constraints or transactions and doesn't run real SQL, so it gives false confidence. Real Postgres makes tests depend on a running DB. |
| Separate `assessment_test` database | Testcontainers | Testcontainers gives better isolation but *requires* Docker. The author's machine has no Docker, only a local Postgres. I'll revisit if test isolation becomes a problem. |

## Auth

_TBD. To be decided when requirements are known._

## Assumptions (ambiguities resolved without asking)

- **"Clean machine"** means the .NET 10 SDK plus either Docker or Postgres is installed. No other global tools are assumed; `dotnet-ef` is a *local* tool restored via `dotnet tool restore`.
- **Dev credentials in `appsettings.Development.json`** (`postgres`/`postgres`) are acceptable because they only match a throwaway local database. Real secrets go in user-secrets or environment variables and are never committed.
- **HTTP-only launch profile for the run instructions.** It avoids requiring `dotnet dev-certs https --trust` on the reviewer's machine.
