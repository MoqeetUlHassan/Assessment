# CLAUDE.md

Guidance for AI coding agents working in this repo.

## Stack
- .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, PostgreSQL 16, xUnit.
- One API project (`src/Assessment.Api`, static client in `wwwroot/`), one test project (`tests/Assessment.Api.Tests`), a browser smoke test (`tests/e2e`).

## Where things live
- `Domain/`: entities and the business rules. `MaintenanceRequest` (aggregate), `ApprovalRules`, `RequestTransitions` (the legal-action table), `Permissions` (catalog). No ASP.NET or EF dependencies.
- `Authorization/`: `SessionValidationMiddleware` (tenant + fresh user per request), `CurrentUser`, permission policies, `MaintenanceRequestAuthorizationHandler` (CanDecide / CanModify).
- `Features/<Feature>/`: endpoints and contracts (Auth, Requests, Sites, Admin, Reports). Endpoints are thin.
- `Infrastructure/Data/`: `AppDbContext` (query filters), `Configurations/`, interceptors (audit, stamping), `Migrations/`, `DevelopmentSeeder`.
- `Infrastructure/Tenancy/`: `TenantContext`, `TenantGuardInterceptor`.

## Commands
- Build: `dotnet build`
- Test: `dotnet test` (needs Postgres on localhost:5432; see README). **On Windows, stop a running dev app first**; it locks the build output.
- Run: `dotnet run --project src/Assessment.Api --launch-profile http`
- Migration: `dotnet tool restore && dotnet ef migrations add <Name> --project src/Assessment.Api`. Check with `dotnet ef migrations has-pending-model-changes`.
- CLI login (password fields are encrypted): `node scripts/login.mjs <email> <password> jar.txt`, then `curl -b jar.txt ...`
- Browser smoke (app running): `cd tests/e2e && npm install && node browser-smoke.js`. Login is rate-limited, so allow a minute between runs.

## Design source of truth
`PLAN.md` holds the agreed design (permissions, approval rules, transition table, data model). `DECISIONS.md` (one page) and `docs/decisions-full.md` hold the rationale. If code and plan disagree, stop and ask. Don't silently pick one.

## Domain & security rules
- **Tenancy:** the org ID comes only from `TenantContext` (the authenticated user), never from a route, query or body. Every tenant entity must implement `ITenantOwned` and gets a global query filter. `IgnoreQueryFilters()` is allowed **only** in the login email lookup; a test enforces this. `TenantContext.UseSystemScope()` is for seeding and test setup only, never for request handling.
- **Authorization:** check **permissions** (`requests.approve`, ...), never role names. Rules that need the record (self-approval, own-request) go in resource-based handlers in `Authorization/`, not in endpoints or entities.
- **Nobody approves their own request or a revision they submitted.** This includes OrgAdmin. There are no exceptions.
- **Approval rules** use the **request's snapshotted threshold**, never the org's current one.
- **Audit:** every state change, decision and admin action raises an `AuditEvent` **from the domain method** (`RaiseAudit`), saved in the same `SaveChanges`. Never write audit rows from endpoints, and never update or delete them.
- **Timestamps and user stamps** come from the interceptor. Never set `CreatedAt/UpdatedAt/CreatedById/UpdatedById` by hand.
- **Illegal transitions** return 409 via the domain's transition table. Don't add ad-hoc status checks in endpoints.
- **Mutating endpoints follow one order:** load through the tenant filter (404) → authorize on the loaded record (403) → domain method (400/409) → save (409 on concurrency). 404 before 403, so a 403 never confirms another tenant's record exists.
- **Password fields are only ever accepted encrypted** via `PasswordFieldEncryption` (challenge → RSA-OAEP over nonce + password), and the password policy is checked after decryption. Never add a plain password field, and never log a password, hash or ciphertext.
- **Client code renders with `textContent`/`el()` only**, never `innerHTML`, and never inline script or style (the CSP forbids them). The server enforces every rule; hiding a button is never the control.

## Rules
- **Never use the EF InMemory provider or SQLite in tests.** Tests run against real Postgres via `ApiFactory`, with a fresh org per test (`TestTenant`).
- **A new test must be seen failing** (plant a bug in the code) before you report it as passing. If a planted bug survives, find out why before moving on.
- **Run the real app before claiming a feature works** (curl walkthrough or the browser smoke). Layer-by-layer tests missed three bugs here that one real request found.
- **Never loosen a security setting to make a test pass** (rate limits, CSP, cookie flags). Fix the test.
- **Don't use evaluation-only APIs or suppress analyzer warnings** (e.g. `[SkipValidation]`, ASP0029) to get past a problem.
- **Schema changes go through EF migrations only.** Never hand-edit a generated migration's snapshot. Raw SQL (like the audit trigger) goes in the migration's `Up`/`Down`. Read the generated SQL before committing.
- **Put entity configuration in `IEntityTypeConfiguration<T>` classes**, not inline in `OnModelCreating`.
- **Never commit secrets.** Dev-only credentials live in `appsettings.Development.json`; everything else goes in user-secrets or environment variables.
- **Don't add NuGet packages without stating why** in the commit message. If the choice is architectural, also add it to `DECISIONS.md` and `docs/decisions-full.md`.
- **Keep commits small and scoped to one concern, and never squash.** The history is part of the deliverable.
- **Don't widen scope.** If the brief is ambiguous, ask, or pick an assumption and record it under "Assumptions made" in `DECISIONS.md`.
- **Keep docs true in the same commit as the change:** README run instructions and API table, the ✅/🔜 markers in `ARCHITECTURE.md`, and `SCHEMA.md` for migrations. `DECISIONS.md` and `AI-LOG.md` stay one page each; details go in `docs/`.

## Style
- Match existing code: file-scoped namespaces, primary constructors, nullable enabled.
- Use comments to explain *why*, not *what*.
- Return errors as ProblemDetails (already registered). Don't write ad-hoc error JSON.

## EF Core / ASP.NET gotchas hit in this repo
- Entity IDs are domain-assigned and configured `ValueGeneratedNever()`. Without that, a new child added to a loaded aggregate is sent as an UPDATE and surfaces as a false 409.
- Revision changes must touch the request row (done in the stamping interceptor) so `xmin` versions the whole aggregate.
- Don't bundle services into an `[AsParameters]` object: .NET 10 validation walks it into the DbContext graph (500). Take services as plain endpoint parameters.
- Anything run at startup must tolerate parallel hosts (test classes run in parallel). Migrations go through `DatabaseMigrator` (an advisory lock on the `postgres` maintenance DB); never call `Database.MigrateAsync()` directly. The seeder takes its own lock, and skips an already-seeded database, so seed changes only reach fresh databases.
- After adding a migration, run the suite on a **brand-new** test database (`DROP DATABASE assessment_test`), not just an already-migrated one. That's the only way the startup race shows up.
- Naming-convention changes rename EF's own `__EFMigrationsHistory` columns too. Databases created before the change must be recreated.
- HSTS is never sent for `localhost` (by ASP.NET Core design); test it with a real host name.
