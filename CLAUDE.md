# CLAUDE.md

Guidance for AI coding agents working in this repo.

## Stack
- .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, PostgreSQL 16, xUnit.
- One API project (`src/Assessment.Api`), one test project (`tests/Assessment.Api.Tests`).

## Commands
- Build: `dotnet build`
- Test: `dotnet test` (needs Postgres on localhost:5432; see README)
- Run: `dotnet run --project src/Assessment.Api --launch-profile http`
- Migration: `dotnet tool restore && dotnet ef migrations add <Name> --project src/Assessment.Api`

## Design source of truth
`PLAN.md` holds the agreed design (permissions, approval rules, transition table, data model). `DECISIONS.md` holds the rationale. If code and plan disagree, stop and ask. Don't silently pick one.

## Domain & security rules
- **Tenancy:** the org ID comes only from `TenantContext` (the authenticated user), never from a route, query or body. Every tenant entity must have a global query filter. `IgnoreQueryFilters()` is allowed **only** in the login email lookup.
- **Authorization:** check **permissions** (`requests.approve`, ...), never role names. Rules that need the record (self-approval, own-request) go in resource-based handlers in `Authorization/`, not in endpoints or entities.
- **Nobody approves their own request or a revision they submitted.** This includes OrgAdmin. There are no exceptions.
- **Approval rules** use the **request's snapshotted threshold**, never the org's current one.
- **Every state change, decision and admin action writes an `AuditEvent` in the same `SaveChanges`.** Never update or delete audit rows.
- **Timestamps and user stamps** come from the interceptor. Never set `CreatedAt/UpdatedAt/CreatedById/UpdatedById` by hand.
- **Illegal transitions** return 409 via the domain's transition table. Don't add ad-hoc status checks in endpoints.

## Rules
- **Never use the EF InMemory provider or SQLite in tests.** Tests run against real Postgres via `ApiFactory`.
- **A new test must be seen failing** (break the code or the input) before you report it as passing.
- **Schema changes go through EF migrations only.** Never hand-edit a generated migration's snapshot.
- **Put entity configuration in `IEntityTypeConfiguration<T>` classes**, not inline in `OnModelCreating`.
- **Never commit secrets.** Dev-only credentials live in `appsettings.Development.json`; everything else goes in user-secrets or environment variables.
- **Don't add NuGet packages without stating why** in the commit message. If the choice is architectural, also add it to `DECISIONS.md`.
- **Keep commits small and scoped to one concern, and never squash.** The history is part of the deliverable.
- **Don't widen scope.** If the brief is ambiguous, ask, or pick an assumption and record it under "Assumptions" in `DECISIONS.md`.
- **Keep README run instructions true.** If you change how the app starts, update the README in the same commit.

## Style
- Match existing code: file-scoped namespaces, primary constructors, nullable enabled.
- Use comments to explain *why*, not *what*.
- Return errors as ProblemDetails (already registered). Don't write ad-hoc error JSON.
