# Decisions

Significant technical choices, what was rejected, and the trade-off. The full design is in [PLAN.md](PLAN.md).

## Architecture

| Decision | Rejected | Trade-off |
|---|---|---|
| **.NET 10 (LTS)**, SDK pinned in `global.json` (`latestFeature`) | .NET 8, .NET 9 | The newest LTS. A reviewer needs any 10.0 SDK. |
| **One API project organised by feature**, plus one test project | Clean Architecture split into 4 assemblies | Proportionate to the scope. The boundaries are folders: `Domain/` (state legality), `Authorization/` (who may act), `Infrastructure/Tenancy` (which data exists). |
| **Minimal APIs, thin endpoints**: bind → validate → authorize → domain method → save | MVC controllers; MediatR | Less ceremony. MediatR and AutoMapper are commercially licensed now and solve problems this app doesn't have. |
| **A hand-written transition table** in the domain | The `Stateless` library | With 5 states, a table is clearer and easier to test exhaustively. |
| **Frontend: static HTML + vanilla JS calling the API** | Razor Pages | One entry point, so authorization is enforced in exactly one place, and there's no build step. |

## Data access & model

| Decision | Rejected | Trade-off |
|---|---|---|
| **PostgreSQL + EF Core 10 (Npgsql)** | SQLite, SQL Server, Dapper | SQLite behaves differently from production. SQL Server is heavy locally. Dapper means hand-written migrations. |
| **Shared schema, with `organization_id` on every tenant row** | Schema-per-tenant or DB-per-tenant | Simple to operate. Isolation has to be enforced in code and constraints (next section). |
| **Composite FKs** `(x_id, organization_id)` | Plain FKs | The DB itself refuses cross-tenant references, even if the app has a bug. It costs an extra unique index per parent. |
| **`request_revisions`**: an immutable snapshot of every edit, cost change and actual cost, with its outcome | Approval columns on the request | The approval record becomes queryable data: who asked for which amount, who decided, and what it replaced. |
| **Threshold snapshotted on the request at creation** | Evaluating every change against the current org threshold | A threshold change can't rewrite how existing requests are treated. |
| **`created_at`/`updated_at` + `created_by_id`/`updated_by_id` on every table**, set by an interceptor | Setting them by hand in each feature | Can't be forgotten. `audit_events` has only `created_at` + actor, because its rows are immutable. |
| **`xmin` optimistic concurrency** | Pessimistic locks | Two Approvers deciding at once: one wins, the other gets 409. |
| **Migrate on startup in Development only** | Always; manual only | One-command start for reviewers. Unsafe with multiple instances, so it's off elsewhere. |

## Tenant isolation

1. At login, the org ID comes from the **user's DB record**, is carried in the session and loaded into a request-scoped `TenantContext`. It's never read from a URL, query string or body.
2. **EF Core global query filters** on every tenant entity make reads org-locked by default. A foreign ID returns 404, not 403, so it doesn't leak that the record exists.
3. A **SaveChanges guard** stamps the org on inserts and throws on any foreign entity.
4. **Composite FKs** in the DB.

**Why at the data layer:** checks written per endpoint get forgotten eventually, while filters are on by default. The only `IgnoreQueryFilters()` is login's email lookup, and a test enforces that.

**Rejected for now: Postgres Row-Level Security.** It's the strongest option, but it needs a per-transaction `SET` on pooled connections plus separate DB roles. It's the first production hardening step.

## Auth & permissions

| Decision | Rejected | Trade-off |
|---|---|---|
| **Cookie session** (HttpOnly, Secure, SameSite=Strict) with `PasswordHasher<T>` | JWT; full ASP.NET Identity | No token storage in the browser and easy revocation, without Identity's 7 tables. |
| **One role per user; roles carry permissions; all checks use permissions** | Hard-coded role checks | OrgAdmin can edit or create non-admin roles. `admin.*` permissions are locked to the OrgAdmin role. |
| **Permissions loaded from the DB on every request** | Permissions baked into the cookie or token | Revocation takes effect on the next request. It costs one indexed query per request. |
| **Self-approval as a permission rule** (resource-based `CanDecideRevision`) | A check inside the entity | "Who may act" is authorization. It applies to **everyone, OrgAdmin included**: no approving your own request, or a revision you submitted. |

**Accepted risk.** OrgAdmin has all permissions. It can raise the threshold and then create a request that's auto-approved, or create a second account to approve its requests. Every step is user-stamped and audited, so the control is **transparency, not prevention** (a product decision). *In production:* four-eyes confirmation for threshold increases and new approver accounts.

## Approval rules (product decisions from review)

- **Initial or edit:** needs approval if `amount >= request threshold` **and** (the cost went up **or** the description changed from the last approved version).
- **Actual cost:** needs approval if `actual >= request threshold`. Completion is blocked until then.
- **An edit while pending** supersedes the pending revision and needs re-approval. Approvers must send the `revisionId` they reviewed; a stale one gets 409.
- **On rejection:** falls back to the last approved content. A rejected actual cost returns the request to Approved. A rejected initial request is terminal.
- **Any one eligible Approver** decides. With no eligible Approver, the request stays pending; it's never auto-approved.

## Audit integrity

- Audit events are written **in the same transaction** as the change.
- There's no update or delete path in the app.
- A **DB trigger blocks UPDATE, DELETE and TRUNCATE** on `audit_events`.
- *In production:* the app's DB role has only INSERT and SELECT on the table, events are shipped to WORM storage, and a hash chain is the next step.

## Testing

Integration tests run against **real Postgres** via `WebApplicationFactory`, and each test creates its own orgs. Rejected: the EF InMemory provider (no constraints, transactions or real SQL) and Testcontainers (needs Docker, which the author's machine doesn't have).

## Assumptions (ambiguities resolved)

- **"Clean machine"** = the .NET 10 SDK, plus Docker **or** Postgres. `dotnet-ef` is a local tool.
- **Dev credentials** (`postgres/postgres`) in `appsettings.Development.json` are fine for a throwaway local DB. Real secrets go in user-secrets or env vars. In production: a secret manager.
- **The run instructions use the HTTP launch profile,** so there's no dev-cert trust step.
- **One currency per org.** Report dates are inclusive and UTC.
- **Equal to the threshold needs approval.**
- **Orgs and sites are seeded;** there's no API for them. Users are created by their OrgAdmin (no self-registration).
- **Requesters edit and complete only their own requests;** `requests.manage` holders can do it for any request.
- **Below the threshold, a description-only edit is auto-approved** and recorded.
- **Emails are globally unique,** so login doesn't need an org code. An admin can learn that an email exists in another org; accepted as low severity.
- **Users are deactivated, never deleted,** because the audit history references them.
