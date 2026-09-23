# Plan: Maintenance Request & Approval Backend

Status: **DRAFT, awaiting review.** Open questions are in §10. Nothing below is built yet.

---

## 1. Scope

**Build:** a multi-tenant JSON API covering auth, the request lifecycle, threshold-based approval, the spend report and the audit trail. Plus a minimal browser client (log in, list requests, create, approve/reject, complete).

**Deliberately not building** (judgment, explained in DECISIONS.md):

| Not building | Why |
|---|---|
| User / org / site management UI or API | Not in the brief. Seeded data covers the flows being assessed, and each admin API widens the attack surface. |
| Self-registration | In a B2B tenant system, open sign-up means someone must be trusted to pick their own org. That's a tenant-isolation hole. |
| Request cancellation, editing, attachments, comments | Not required. Each adds states and transitions. |
| Multi-currency | Assume one currency per org. Amounts are `numeric(12,2)`. |
| JWT, refresh tokens, OAuth/SSO | Cookie auth covers the same-origin client and curl (§5). |
| Postgres Row-Level Security | Considered as a 3rd isolation layer (§4). Held back as a production hardening step. |

---

## 2. Architecture

A single ASP.NET Core project, organised **by feature**, with domain rules in the entity and not in endpoints:

```
src/Assessment.Api/
  Domain/            MaintenanceRequest (state machine), Organization, Site, User, AuditEvent, enums
  Features/
    Auth/            login, logout, me
    Requests/        create, list, get, approve, reject, complete, review-cost, history
    Reports/         spend-by-site
  Infrastructure/
    Data/            AppDbContext, entity configs, migrations, seed
    Tenancy/         ITenantContext (from claims), query filters, SaveChanges guard
  wwwroot/           static HTML + vanilla JS client
```

- **Endpoints are thin.** They bind, validate, call a domain method, and save. All business rules live in `MaintenanceRequest`, so they're unit-testable without HTTP or a DB.
- **No MediatR, AutoMapper, repository layer or state-machine library.** With 5 states, a transition table is clearer than `Stateless`. `DbContext` already acts as the unit of work and repository. MediatR and AutoMapper are commercially licensed now, and they solve problems this app doesn't have.
- **The frontend is a pure API client** (static files, same origin, `fetch`). There's **one entry point** (the API), so authorization lives in exactly one place. Razor Pages was rejected because it would need a second set of handlers calling the same services, and a second place to get authz wrong.

---

## 3. Data model

Shared database, shared schema, and an `organization_id` column on every tenant-owned row. IDs are `Guid.CreateVersion7()`: sortable, and not enumerable. **Isolation does not rely on IDs being unguessable**; the tests use real foreign IDs.

```
organizations      id, name, approval_threshold numeric(12,2) CHECK >= 0, created_at

sites              id, organization_id → organizations, name
                   UNIQUE (organization_id, name)
                   UNIQUE (id, organization_id)            -- target for composite FKs

users              id, organization_id → organizations, email (UNIQUE, lower-cased),
                   display_name, password_hash, role (Requester|Approver), is_active, created_at
                   UNIQUE (id, organization_id)

maintenance_requests
                   id, organization_id, site_id, requested_by_id,
                   description varchar(2000), estimated_cost numeric(12,2),
                   status (Raised|PendingApproval|Approved|Rejected|Completed),
                   threshold_at_submission numeric(12,2), approval_mode (Auto|Manual),
                   decided_by_id, decided_at, decision_reason,
                   actual_cost numeric(12,2), completed_at, completed_by_id,
                   requires_cost_review bool, cost_reviewed_by_id, cost_reviewed_at,
                   created_at, xmin (optimistic concurrency)
                   FK (site_id, organization_id)         → sites(id, organization_id)
                   FK (requested_by_id, organization_id) → users(id, organization_id)
                   FK (decided_by_id, organization_id)   → users(id, organization_id)
                   CHECK costs >= 0; CHECK status='Completed' ⇒ actual_cost, completed_at NOT NULL

audit_events       id bigint identity, organization_id, entity_type, entity_id, action,
                   from_status, to_status, actor_user_id (NULL = system), occurred_at,
                   details jsonb   -- e.g. costs, threshold, reason
                   TRIGGER: reject UPDATE / DELETE / TRUNCATE
```

**Why composite FKs:** even if application code has a bug, the database refuses a request whose site or user belongs to another org. It's cheap and it's the last line of defence.

**Indexes, each tied to a real query:**
- `maintenance_requests (organization_id, status, created_at DESC)`: the list and approval queue.
- `maintenance_requests (organization_id, site_id, completed_at) INCLUDE (actual_cost) WHERE status = 'Completed'`: the spend report, a partial covering index.
- `audit_events (organization_id, entity_id, occurred_at)`: request history.
- `users (lower(email))` unique: login.

Enums are stored as **text**, so the DB and audit rows stay readable without the code. Migrations are EF Core, one per milestone. The audit trigger lives in a migration as raw SQL.

---

## 4. Tenant isolation: where and why

| Layer | Mechanism | What it catches |
|---|---|---|
| 1. Identity | `OrganizationId` comes **only** from the auth cookie's claims (`ITenantContext`), never from the route, query string or body. | Callers claiming a different tenant. |
| 2. Reads | **EF Core global query filter** on every tenant entity: `e.OrganizationId == tenant.OrganizationId`. | A forgotten `.Where(...)`. Every query is scoped by default, so a foreign ID returns **404** (not 403, which would leak that it exists). |
| 3. Writes | A `SaveChanges` guard stamps `OrganizationId` on new entities and **throws** if any tracked entity's org ≠ the current tenant. | Code that loads or attaches something it shouldn't. |
| 4. Database | Composite FKs (§3). | Cross-tenant references from bugs in the layers above. |

**Why the data layer and not endpoints/controllers:** checks written per endpoint get forgotten on the 12th endpoint. A query filter is on by default and needs a deliberate `IgnoreQueryFilters()` to switch off. That appears in exactly **one** place (login, which must find a user by email before a tenant is known). A test enforces that it's the only occurrence.

**Rejected: Postgres RLS.** It's the strongest option, but it needs a `SET app.org_id` per transaction on pooled connections and separate DB roles for migrations vs the app, and it makes local setup harder. I'd add it in production as defence in depth; it's noted in DECISIONS.md.

---

## 5. Authentication & authorization

- **Cookie auth** (ASP.NET Core cookie handler) with our own `users` table. Passwords are hashed with Identity's `PasswordHasher<T>` (PBKDF2, versioned) without pulling in Identity's 7-table schema.
  - Cookie settings: `HttpOnly`, `Secure`, `SameSite=Strict`, 8h sliding expiry.
  - Every 5 minutes the principal is revalidated against the DB. Deactivating a user or changing their role takes effect without waiting for the cookie to expire.
- **CSRF:** `SameSite=Strict` plus mutations that only accept `application/json`, which a cross-site HTML form can't send without a CORS preflight, and CORS isn't enabled.
- **Rejected JWT:** there's no clean revocation, token storage in the browser is a liability, and there's no second client that needs it.
- **Login hardening:** the same generic error for an unknown email and a wrong password; a fixed-window rate limit on `/api/auth/login` (built-in `RateLimiter`); failed logins are logged.
- **Authorization, in two layers:**
  1. **Endpoint policies** give coarse role checks, e.g. `RequireRole("Approver")` on approve/reject/review-cost and on the report. These return 403.
  2. **Domain rules** cover facts that need the entity loaded: "an Approver cannot decide their own request", "only the requester or an Approver can complete". These are enforced inside `MaintenanceRequest`, so no entry point can skip them.

**Permissions matrix** (assumptions marked *):

| Action | Requester | Approver |
|---|---|---|
| Raise a request | ✅ | ✅ * |
| List / view requests | own only * | all in org |
| Approve / reject | ❌ | ✅ if not their own |
| Complete (record actual cost) | own only | ✅ |
| Acknowledge a cost overrun | ❌ | ✅ if not their own |
| Spend report | ❌ * | ✅ |
| View audit history | own requests | all in org |

---

## 6. Request lifecycle

```
            est < threshold (system)
  Raised ───────────────────────────► Approved ──complete(actual)──► Completed
    │                                    ▲                              (terminal)
    │ est >= threshold (system)          │ approve (Approver ≠ requester)
    └──────────────► PendingApproval ────┤
                                         └─ reject(reason) ──► Rejected (terminal)
```

- **Raised is a real, audited state, but transient.** `POST /requests` creates the request and routes it in one transaction, producing two audit events: `Raised`, then `AutoApproved` or `SubmittedForApproval`. This means the frontend doesn't need a separate "submit" step.
- **Threshold:** a cost **equal to** the threshold requires approval (the brief is silent on this; I took the conservative reading). The threshold is **snapshotted** onto the request, so changing it later doesn't rewrite history.
- **Enforcement:** a single transition table in the domain. Any other `(state, action)` pair throws `InvalidTransition` and returns **409**. Concurrent decisions on the same request are caught by `xmin` optimistic concurrency, so the second writer gets 409.

**Cost overrun (the brief's open choice).** A request's *authorised ceiling* is:
- the **threshold** if it was auto-approved (anything under it needed no sign-off), or
- the **approved estimate** if an Approver signed it off.

On `complete`, if `actual_cost` exceeds the ceiling:
- the request still becomes **Completed** (the money is already spent, so blocking completion would only hide it);
- it gets `requires_cost_review = true` and a `CostOverrunFlagged` audit event;
- an Approver other than the requester must **acknowledge** it (`CostReviewed` audit event);
- the spend report counts it (spend is spend) and returns a count of unreviewed overruns per site.

*Rejected: a `PendingCostApproval` state that blocks completion.* "Rejecting" work that has already been paid for doesn't mean anything. It also closes the loophole this rule is really for: someone who **under-estimates to skip approval** gets flagged, and the flag is attached to their name.

---

## 7. API

| Method | Route | Notes |
|---|---|---|
| POST | `/api/auth/login` · `/api/auth/logout` | |
| GET | `/api/me` | user, role, org, threshold |
| GET | `/api/sites` | for the create form |
| GET | `/api/requests?status=&siteId=&page=` | paged, scoped per §5 |
| POST | `/api/requests` | `{ siteId, description, estimatedCost }` |
| GET | `/api/requests/{id}` | |
| POST | `/api/requests/{id}/approve` | `{ comment? }` |
| POST | `/api/requests/{id}/reject` | `{ reason }` (required) |
| POST | `/api/requests/{id}/complete` | `{ actualCost }` |
| POST | `/api/requests/{id}/cost-review` | acknowledge an overrun |
| GET | `/api/requests/{id}/history` | audit events |
| GET | `/api/reports/spend?from=2026-08-01&to=2026-08-31` | |

**Spend report semantics:**
- Spend = sum of `actual_cost` of **Completed** requests, bucketed by `completed_at`.
- `from`/`to` are **inclusive dates in UTC**.
- Every site in the org is returned, including sites with zero spend. Response per site: `siteId, siteName, totalSpend, completedCount, unreviewedOverrunCount`.
- It's a single grouped SQL query (`LEFT JOIN` sites), not an in-memory aggregation.

**Errors** are `ProblemDetails` everywhere: 400 validation, 401 unauthenticated, 403 wrong role or own request, 404 not found / other tenant, 409 illegal transition / concurrency.

---

## 8. Validation, audit integrity, secrets

**Input validation at the boundary** uses .NET 10's built-in minimal-API validation (DataAnnotations), with no FluentValidation dependency:
- description 1–2000 chars, trimmed;
- costs > 0 and ≤ 10,000,000 with at most 2 decimals;
- `from ≤ to` and a range ≤ 366 days;
- page size capped;
- request body size limit.

The domain re-checks the invariants. EF parameterises all SQL. The frontend renders only via `textContent` (never `innerHTML`), with a CSP of `default-src 'self'`.

**Audit integrity: who can modify it?**
- **Through the app, nobody.** There's no update/delete code path and no endpoint.
- **Atomicity:** audit rows are produced by the domain methods and saved in the **same transaction** as the state change. A state change can't be persisted without its audit row, or the reverse.
- **At the database:** a trigger raises on `UPDATE`, `DELETE` and `TRUNCATE` of `audit_events`, even when the app's DB user sends them. An integration test proves this with raw SQL.
- **In production:** the app connects as a role with only `INSERT, SELECT` on `audit_events`, and the table owner is a separate migration role. Events are streamed to append-only / WORM storage so that even a DBA can't rewrite history unnoticed. A per-org hash chain was considered for tamper *evidence*; it's listed as next step, not built.

**Secrets:**

| | Local | Production |
|---|---|---|
| DB credentials | `appsettings.Development.json` (throwaway local `postgres/postgres`), overridable via user-secrets or env var | Secret manager (Azure Key Vault / AWS Secrets Manager) injected as env vars, or managed identity; nothing in config files |
| Cookie-signing keys | Data Protection default key ring in the user profile | Persisted to shared storage and encrypted with a KMS key, so keys survive restarts and are shared across instances |
| Seed users | Dev-only passwords documented in the README; seeding runs **only** in Development | No seeding. Real users are provisioned. |

Nothing sensitive is committed. That's checked before each push.

---

## 9. Testing: what, and why those

Tests run against real Postgres. **Each test creates its own fresh orgs**, so tests are isolated from each other with no cleanup library needed.

| Test | Why this one |
|---|---|
| **Transition table**: a theory over every `(state × action)` pair, where legal ones succeed and all others throw | The brief explicitly says "enforce that". This covers the *whole* table, not a few happy paths. |
| **Threshold boundary**: below / equal / above | Off-by-one at the boundary is the likeliest bug. |
| **Self-approval**: an Approver approving or rejecting their own request is refused | A named requirement that's easy to miss if only roles are checked. |
| **Cost overrun rule**: auto vs manual ceiling | This is my own documented choice, so it needs to be pinned down. |
| **Cross-tenant by ID**: a user in org B does GET / approve / complete / history on org A's request id → 404; creates a request with org A's siteId → rejected; org A's data never appears in B's report | The headline security requirement, tested the way an attacker would try it. |
| **Every tenant entity has a query filter** (reflection over the EF model) | Catches the *future* entity someone adds without a filter. |
| **`IgnoreQueryFilters` appears only in login** | Keeps the escape hatch from spreading. |
| **Role enforcement over HTTP**: a Requester approving → 403; unauthenticated → 401 | Proves authz is server-side, not a hidden button. |
| **Spend report numbers**: in-range vs boundary dates, non-completed excluded, zero-spend sites included, other org excluded | "Returns the right numbers" is graded, and date boundaries are where reports go wrong. |
| **Audit is atomic and immutable**: an approval writes exactly one matching event; raw-SQL `UPDATE`/`DELETE` on `audit_events` fails | Compliance claims need proof at the DB level, not just in the app. |
| **Concurrent approve + reject** → one wins, the other gets 409 | A realistic race with two approvers. |

**Not tested, deliberately:** framework behaviour (model binding, EF mapping trivia, per-field validation attributes), the static frontend, and logging. Low risk, and tests there would just mirror the code.

---

## 10. Open questions (please decide; defaults marked)

1. **Can Approvers raise requests?** Default: **yes** (the brief's "cannot approve their own request" implies they can).
2. **What can a Requester see?** Default: **only their own** requests (least privilege). Alternative: all requests in their org.
3. **Who can change the threshold?** Default: **no API.** It's set per org in seed/DB and snapshotted on each request. An Approver who could change it could lower the bar, then raise their own request under it: a self-approval bypass. The alternative is a separate OrgAdmin role that can't approve.
4. **Does a cost equal to the threshold need approval?** Default: **yes**.
5. **Cost overrun:** default **complete + flag + Approver acknowledgement** (§6). Alternative: block completion until re-approved.
6. **Frontend:** default **static HTML + vanilla JS** calling the API (no build step, one authz path). Alternative: Razor Pages.
7. **Spend report access:** default **Approvers only**. Alternative: any user in the org.

---

## 11. Execution order (one or more commits each, pushed as I go)

1. **Domain:** entities, the transition table, the overrun rule, plus unit tests. *Transition table: I write it by hand, then the agent writes the tests from it.*
2. **Schema:** EF configs, the first migration, composite FKs, CHECKs, indexes, the audit trigger. *I review the generated migration SQL line by line.*
3. **Tenancy:** `ITenantContext`, query filters, the SaveChanges guard, plus isolation meta-tests.
4. **Auth:** cookie login, revalidation, rate limit, role policies, dev seed (2 orgs × sites × Requester / Approver / Approver2).
5. **Request endpoints** with validation and ProblemDetails, plus HTTP-level authz and cross-tenant tests.
6. **Spend report** plus number tests. *I verify the numbers against a hand-computed fixture.*
7. **Audit history endpoint** plus the immutability test.
8. **Frontend:** the minimal static client.
9. **Docs:** update DECISIONS / README / AI-LOG, then a clean-clone run timed against the 15-minute budget.
