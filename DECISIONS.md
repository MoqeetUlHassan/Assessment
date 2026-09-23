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
| **Pending / last-approved revision derived from `request_revisions`**, with a partial unique index `(request_id) WHERE outcome='Pending'` | `pending_revision_id` / `approved_revision_id` columns on the request | No circular FK between the two tables, which EF handles badly on insert. The DB still guarantees at most one pending revision. |
| **Revisions auto-included** whenever a request is loaded | Explicit `Include` at each call site | The aggregate's rules need its revisions. A forgotten `Include` would silently give wrong answers (e.g. "nothing pending"). List endpoints use projections, which skip it. |
| **Role permissions as a `text[]` column** | A `role_permissions` join table | Permissions are only read per role, never queried across roles. |
| **snake_case identifiers** via `EFCore.NamingConventions` | EF's default PascalCase | Idiomatic Postgres, with no quoted identifiers in raw SQL, CHECKs or triggers. |
| **No FKs on `created_by_id` / `updated_by_id`** | Composite FKs to users | The `organizations` table has no org column to make them composite, and the audit trail (which *is* FK'd) is the authoritative who. |
| **Migrate on startup in Development only** | Always; manual only | One-command start for reviewers. Unsafe with multiple instances, so it's off elsewhere. |

## Tenant isolation

1. At login, the org ID comes from the **user's DB record**, is carried in the session and loaded into a request-scoped `TenantContext`. It's never read from a URL, query string or body.
2. **EF Core global query filters** on every tenant entity make reads org-locked by default. A foreign ID returns 404, not 403, so it doesn't leak that the record exists.
3. A **SaveChanges guard** *verifies* every added, modified or deleted row belongs to the caller's org, and throws otherwise. It refuses tenantless writes and any audit change.
   - It verifies rather than stamps, so a wrong org is a bug surfaced, not silently corrected.
   - Seeding uses an explicit **system scope**, which is never reachable from HTTP.
   - With no tenant, **reads return nothing** (the filter compares against null), so the system fails closed.
4. **Composite FKs** in the DB.

**Why at the data layer:** checks written per endpoint get forgotten eventually, while filters are on by default. The only `IgnoreQueryFilters()` is login's email lookup, and a test enforces that.

**Rejected for now: Postgres Row-Level Security.** It's the strongest option, but it needs a per-transaction `SET` on pooled connections plus separate DB roles. It's the first production hardening step.

## Auth & permissions

| Decision | Rejected | Trade-off |
|---|---|---|
| **Cookie session** (HttpOnly, Secure, SameSite=Strict) with `PasswordHasher<T>` | JWT; full ASP.NET Identity | No token storage in the browser and easy revocation, without Identity's 7 tables. |
| **One role per user; roles carry permissions; all checks use permissions** | Hard-coded role checks | OrgAdmin can edit or create non-admin roles. `admin.*` permissions are locked to the OrgAdmin role. |
| **Permissions loaded from the DB on every request** | Permissions baked into the cookie or token | Revocation takes effect on the next request. It costs one indexed query per request. |
| **Session stamp = hash of the password hash**, carried in the cookie and checked on every request | A `security_stamp` column | A password reset ends every existing session without a schema change. The stamp isn't secret; it only has to change when the hash does. |
| **Login: identical 401 for unknown email, wrong password and deactivated account.** Unknown emails are still verified against a dummy hash | Distinct errors; early return | No account enumeration by message or by timing. |
| **Login rate limit per client IP** (built-in `RateLimiter`, 10/min, configurable) | Account lockout | Lockout lets anyone lock out a known user. The IP limit slows guessing without that denial of service. |
| **Cookie `Secure` only outside Development** | Always `Secure` | Dev runs on plain HTTP so reviewers skip the dev-cert step. Everywhere else the cookie is HTTPS-only. |
| **Every mutating endpoint goes through one helper**: load (→ 404) → authorize on the record (→ 403) → domain (→ 400/409) → save (→ 409) | Per-endpoint ordering | 404 before 403 means a 403 can never confirm another org's request exists. There's one place to get the order right. |
| **Another org's `siteId` on create → 400 "Site not found"** | 403 / 404 | It's input validation against the caller's own sites. It's indistinguishable from a site that doesn't exist. |
| **A revision change also marks its request row modified** | An `xmin` on each revision | One version per aggregate. Resubmitting an actual cost only touches revisions; without this, it could commit alongside a concurrent approval. There's a forced-interleaving test. |
| **Entity IDs are `ValueGeneratedNever`** | EF's default for Guid keys | The domain assigns the IDs. With the default, a *new* revision added to a loaded request was sent as an UPDATE and surfaced as a false 409. Found by a manual walkthrough. |
| **Endpoint services as plain parameters** | An `[AsParameters]` service bundle, or `[SkipValidation]` | .NET 10 validation walks `[AsParameters]` objects into the DbContext graph (500). `[SkipValidation]` is marked evaluation-only (ASP0029), so I wouldn't depend on it. |
| **Strict CSP on every response** (`script-src 'self'`, no `unsafe-inline`), plus nosniff, `frame-ancestors 'none'`, no-referrer | A looser CSP to allow inline script | The client renders all user text via `textContent`. The CSP is the second layer: even a future rendering mistake couldn't run injected script. It forces scripts and styles into files. |
| **Client built earlier than planned (before admin/report)**, at the user's request, to test the flows by hand | Holding it until step 8 | Pages exist for every API that exists. The admin panel and report pages come with their endpoints. |
| **One admin endpoint per action** (role, deactivate, reactivate, password) | One generic `PUT /users/{id}` | Each action raises its own audit event with before/after, and each has its own guardrail. |
| **No "last active OrgAdmin" check**, deliberately | The check from the plan | It can never trigger: `admin.users` is locked to the OrgAdmin role, and admins can't deactivate or demote themselves, so the acting admin always remains. Documented in `User.cs`. If admin permissions ever became grantable, this would need revisiting. |
| **Organization audit log for admins** (`GET /api/admin/audit`) | Per-request history only | It's the control for the accepted OrgAdmin risk: a threshold change, users created (with `createdBy`), and permission edits are all visible in one timeline. |
| **Dev seeder serialised with a Postgres advisory lock** | An unguarded check-then-insert | Parallel test hosts (or two dev instances) deadlocked seeding the same rows. It was found by the test suite, not in theory. |
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
