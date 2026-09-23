# Plan: Maintenance Request & Approval Backend

Status: **v2, review feedback applied.** Decisions are recorded in §10. Nothing is built yet.

---

## 1. Scope

**Build:**
- a multi-tenant JSON API covering auth, the request lifecycle, threshold approval, **cost revisions with re-approval**, **org threshold management (OrgAdmin)**, the spend report and a full per-request audit history;
- a minimal static HTML + JS client.

**Deliberately not building:**

| Not building | Why |
|---|---|
| User / org / site management API | Not in the brief. Seeded data covers the assessed flows. Every admin API is more attack surface. |
| Self-registration | Letting someone pick their own tenant is an isolation hole in B2B. |
| Cancellation, attachments, comments | Not required. Each adds states. |
| Multi-currency | One currency per org, `numeric(12,2)`. |
| JWT, refresh tokens, SSO | The cookie session issued at login covers the same-origin client and curl (§5). |
| Postgres Row-Level Security | A candidate 3rd isolation layer (§4). Kept as production hardening. |

---

## 2. Architecture

A single ASP.NET Core project, organised by feature:

```
src/Assessment.Api/
  Domain/            MaintenanceRequest (state machine), CostRevision, Organization, Site, User, AuditEvent
  Authorization/     permission requirements + resource-based handlers (e.g. CanDecideRevision)
  Features/
    Auth/            login, logout, me
    Requests/        create, list, get, edit, revise-cost, complete, approve, reject, history
    Organization/    get settings, change threshold (OrgAdmin)
    Reports/         spend-by-site
  Infrastructure/
    Data/            AppDbContext, configs, migrations, seed, timestamp interceptor
    Tenancy/         TenantContext (request-scoped), query filters, SaveChanges guard
  wwwroot/           static HTML + vanilla JS client
```

Responsibilities are split three ways:
- **Permissions** (*who* may do something): `Authorization/`.
- **Lifecycle rules** (*what* is legal from which state): `Domain/`.
- **Tenant scoping** (*which data exists* for you at all): `Infrastructure/Tenancy`.

Endpoints are thin: bind → validate → authorize → call a domain method → save.

**Not used:** MediatR, AutoMapper, a repository layer, `Stateless`. With 5 states, a transition table is clearer than a library, `DbContext` is already the unit of work, and MediatR and AutoMapper are commercially licensed now. **The frontend only calls the API**, so there's one entry point and one place where authorization happens.

---

## 3. Data model

Shared schema, with `organization_id` on every tenant-owned row. IDs are `Guid.CreateVersion7()`. Isolation **does not** rely on IDs being unguessable.

**Every table has `created_at` and `updated_at`** (`timestamptz`, UTC), set by a SaveChanges interceptor from an injected `TimeProvider` so tests control the clock. The one exception is `audit_events`, which has `created_at` only: its rows can never change (§8), so `updated_at` would be meaningless.

```
organizations      id, name, approval_threshold numeric(12,2) NOT NULL DEFAULT 10000 CHECK >= 0,
                   created_at, updated_at

sites              id, organization_id, name, created_at, updated_at
                   UNIQUE (organization_id, name), UNIQUE (id, organization_id)

users              id, organization_id, email (UNIQUE on lower(email)), display_name, password_hash,
                   role (Requester|Approver|OrgAdmin), is_active, created_at, updated_at
                   UNIQUE (id, organization_id)

maintenance_requests
                   id, organization_id, site_id, requested_by_id, description varchar(2000),
                   status (Raised|PendingApproval|Approved|Rejected|Completed),
                   estimated_cost numeric(12,2)      -- current estimate
                   approved_amount numeric(12,2)     -- highest amount currently authorised (0 until first approval)
                   actual_cost numeric(12,2) NULL,
                   pending_revision_id NULL → cost_revisions,
                   completed_at NULL, created_at, updated_at, xmin (optimistic concurrency)
                   FK (site_id, organization_id)         → sites(id, organization_id)
                   FK (requested_by_id, organization_id) → users(id, organization_id)
                   CHECK costs >= 0
                   CHECK (status = 'PendingApproval') = (pending_revision_id IS NOT NULL)
                   CHECK status = 'Completed' ⇒ actual_cost, completed_at NOT NULL

cost_revisions     -- every money change on a request: the initial estimate, estimate changes, the actual cost
                   id, organization_id, request_id, kind (Initial|EstimateChange|ActualCost),
                   previous_amount, new_amount, reason, submitted_by_id,
                   threshold_at_submission, outcome (Pending|AutoApproved|Approved|Rejected),
                   decided_by_id NULL, decided_at NULL, decision_comment NULL,
                   created_at, updated_at
                   FK (request_id, organization_id)      → maintenance_requests(id, organization_id)
                   FK (submitted_by_id, organization_id) → users(id, organization_id)
                   FK (decided_by_id, organization_id)   → users(id, organization_id)
                   CHECK decided_by_id <> submitted_by_id

audit_events       id bigint identity, organization_id, entity_type, entity_id, action,
                   from_status, to_status, actor_user_id NULL (NULL = system), details jsonb, created_at
                   TRIGGER: reject UPDATE / DELETE / TRUNCATE
```

**`cost_revisions` is the approval record.** Each row answers "who asked for this amount, who approved it (or was it automatic), when, and against which threshold". The approval history is data in its own right, not just log lines.

**Composite FKs** mean the database itself refuses a request, revision or approval that points at a site or user in another org, even if the code has a bug.

**Indexes, each tied to a real query:**
- `maintenance_requests (organization_id, status, created_at DESC)`: the list and approval queue.
- `maintenance_requests (organization_id, site_id, completed_at) INCLUDE (actual_cost) WHERE status = 'Completed'`: the spend report.
- `cost_revisions (request_id, created_at)`: revision history.
- `audit_events (organization_id, entity_id, created_at)`: request history.
- `users (lower(email))` unique: login.

---

## 4. Tenant isolation

**How the org ID is established:**
1. On first page load there's no session, so the API returns 401 and the client shows the login form.
2. `POST /api/auth/login` checks the credentials, **loads the user from the database** and reads *that user's* `OrganizationId` and role from their DB record.
3. It issues an HttpOnly session cookie carrying `userId`, `orgId` and `role`.
4. The client calls `GET /api/me` for the user details it displays.

The org ID is **never** read from a URL, query string or body a client could edit.

**Org-scoped data access:**

| Layer | Mechanism | What it catches |
|---|---|---|
| 1. Request scope | A request-scoped `TenantContext` is populated from the authenticated user. All data access goes through it. | Callers claiming another tenant. |
| 2. Reads | An **EF Core global query filter** on every tenant entity (`OrganizationId == TenantContext.OrganizationId`). Every query is org-locked automatically. | A forgotten `.Where(...)`. A foreign ID simply doesn't exist, so the API returns **404** (not 403, which would leak that it exists). |
| 3. Writes | A SaveChanges guard stamps `OrganizationId` on inserts and **throws** if any tracked entity belongs to another org. | Code attaching or modifying foreign data. |
| 4. Database | Composite FKs (§3). | Cross-tenant references from bugs in the layers above. |

**Why at the data layer:** a check written per endpoint gets forgotten eventually, while a query filter is on by default. The only bypass, `IgnoreQueryFilters()`, is allowed in exactly one place (login's email lookup, before a tenant is known). A test enforces that.

---

## 5. Authentication & permissions

**Session:**
- Cookie auth against our own `users` table. Passwords are hashed with Identity's `PasswordHasher<T>` (PBKDF2), without Identity's 7-table schema.
- The cookie is `HttpOnly`, `Secure` and `SameSite=Strict`, with an 8h sliding expiry.
- The user is **revalidated against the DB every 5 minutes**, so a deactivation or role change takes effect without waiting for the cookie to expire.
- **CSRF:** SameSite=Strict, JSON-only mutations, and no CORS.
- **Login:** a generic error message, a rate limit on the endpoint, and failures are logged.
- **Rejected JWT:** revocation is hard, token storage in the browser is a risk, and there's no second client that needs it.

**Permissions are enforced as ASP.NET Core authorization, never in the UI:**
- **Role policies** at the endpoint handle "is this an Approver / OrgAdmin" and return 403.
- **Resource-based authorization handlers** handle rules that need the loaded record. `CanDecideRevision` checks:
  - the caller is an **Approver**;
  - the caller is **not the request's creator**;
  - the caller is **not the submitter of the revision being decided**;
  - the request is in the caller's org (already guaranteed by §4; asserted again anyway).

  Failing any of these returns **403**. So "you can't approve your own request" is a **permission**, just as you asked, and the domain entity handles only state legality.

| Action | Requester | Approver | OrgAdmin |
|---|---|---|---|
| Raise a request | ✅ | ✅ | ❌ |
| List / view requests + history (whole org) | ✅ | ✅ | ✅ |
| Edit description (not while pending approval) | ✅ | ✅ | ❌ |
| Revise estimate / record actual cost | ✅ | ✅ | ❌ |
| Approve / reject a pending revision | ❌ | ✅ if not requester and not revision submitter | ❌ |
| Spend report | ❌ | ✅ | ❌ |
| View / change the org threshold | view | view | ✅ change |

OrgAdmin is **deliberately separate** from Approver (separation of duties). Whoever can raise the threshold can't approve requests or raise them.

---

## 6. Request lifecycle & approval rules

**One rule decides whether any money change needs approval:**

```
needsApproval = newAmount >= threshold  AND  newAmount > approvedAmount
```

- The **initial estimate** starts with `approvedAmount = 0`, so it needs approval exactly when it's at or above the threshold (an amount *equal* to the threshold requires approval).
- **An increase** needs a fresh approval whenever the new total reaches or passes the threshold, even if the request was approved before (e.g. approved at 12k, raised to 13k → approve again).
- **An increase that stays under the threshold, or any decrease**, is auto-approved and recorded as such.
- **The actual cost** is treated the same way. If it exceeds what was authorised *and* is at or above the threshold, **completion is blocked** until an Approver signs off.
- The threshold in force at the time is **snapshotted onto each revision**, so a later threshold change never rewrites past decisions.
- **Any one eligible Approver** can approve or reject.

**State transitions.** Anything not listed returns **409**.

| From | Action | Condition | To |
|---|---|---|---|
| — | create | — | Raised (transient, audited) |
| Raised | route (system) | initial estimate needs no approval | Approved (revision AutoApproved) |
| Raised | route (system) | needs approval | PendingApproval (Initial) |
| PendingApproval (Initial) | approve / reject | CanDecideRevision | Approved / **Rejected** (terminal) |
| Approved | revise estimate | no approval needed | Approved (revision AutoApproved) |
| Approved | revise estimate | needs approval | PendingApproval (EstimateChange) |
| PendingApproval (EstimateChange) | approve / reject | CanDecideRevision | Approved (new amount) / Approved (old amount kept) |
| Approved | complete with actual | no approval needed | **Completed** (terminal) |
| Approved | complete with actual | needs approval | PendingApproval (ActualCost): **completion blocked** |
| PendingApproval (ActualCost) | approve / reject | CanDecideRevision | Completed / Approved (actual cleared, resubmit) |

- **Only one pending revision at a time.** No edits or revisions are allowed while PendingApproval, so the Approver always decides on content that can't change under them.
- Approve/reject calls must include the **`revisionId`** the Approver was looking at. A stale ID returns 409, so nobody approves an amount they didn't see.
- Concurrent decisions are caught by `xmin`: the second writer gets 409.
- If an org has no eligible Approver (e.g. the only Approver raised the request), the request **stays PendingApproval**. It's never auto-approved. The API response says so.

---

## 7. API

| Method | Route | Who | Notes |
|---|---|---|---|
| POST | `/api/auth/login` · `/api/auth/logout` | anyone | login returns the user + org |
| GET | `/api/me` | authenticated | user, role, org, threshold |
| GET | `/api/sites` | authenticated | |
| GET | `/api/requests?status=&siteId=&page=` | authenticated | whole org, paged |
| POST | `/api/requests` | Requester, Approver | `{ siteId, description, estimatedCost }` |
| GET | `/api/requests/{id}` | authenticated | includes the pending revision |
| PATCH | `/api/requests/{id}` | Requester, Approver | `{ description }`; not while pending |
| POST | `/api/requests/{id}/cost-revisions` | Requester, Approver | `{ newEstimate, reason }` |
| POST | `/api/requests/{id}/complete` | Requester, Approver | `{ actualCost, reason? }` |
| POST | `/api/requests/{id}/approve` | Approver + CanDecideRevision | `{ revisionId, comment? }` |
| POST | `/api/requests/{id}/reject` | Approver + CanDecideRevision | `{ revisionId, reason }` (required) |
| GET | `/api/requests/{id}/history` | authenticated | audit events + revisions |
| GET | `/api/organization` | authenticated | name, threshold |
| PUT | `/api/organization/threshold` | OrgAdmin | `{ amount, reason }` |
| GET | `/api/reports/spend?from=&to=` | Approver | |

**Spend report:**
- Sum of `actual_cost` for **Completed** requests, bucketed by `completed_at`.
- `from`/`to` are **inclusive UTC dates**.
- **Every site** in the org is returned, including sites with zero spend: `siteId, siteName, totalSpend, completedCount`.
- It's one grouped SQL query with a `LEFT JOIN` from sites.

**Errors:** `ProblemDetails`. 400 validation · 401 · 403 permission · 404 missing or other tenant · 409 illegal transition, stale revision or concurrency.

---

## 8. Audit trail (compliance)

**Every** state change and money decision writes an `audit_events` row **in the same transaction** as the change itself. They are produced by the domain methods, so no code path can change state without also writing the audit row.

The per-request history answers who / what / when for:

| Event | Actor | Details |
|---|---|---|
| `Raised` | requester | site, description, estimate |
| `AutoApproved` | **system** | amount, threshold snapshot, `triggeredBy` user |
| `SubmittedForApproval` | submitter | revision kind, amount, threshold |
| `Approved` / `Rejected` | approver | revision id, amount, comment / reason |
| `DescriptionEdited` | editor | before / after |
| `CostRevisionSubmitted` | editor | old → new, reason |
| `ActualCostSubmitted` | submitter | actual vs authorised |
| `Completed` | submitter or approver | final actual |
| `ThresholdChanged` (org-level) | OrgAdmin | old → new, reason |

**Integrity: who can modify the audit trail?**
- **Through the app, nobody.** There's no update/delete code path and no endpoint.
- **At the DB:** a trigger rejects `UPDATE`, `DELETE` and `TRUNCATE` on `audit_events`, even from the app's own DB user. A raw-SQL test proves this.
- **In production:** the app connects as a role granted only `INSERT, SELECT` on `audit_events`, and a separate migration role owns the table. Events are also shipped to append-only / WORM storage, so a DBA can't rewrite history unnoticed. A per-org hash chain for tamper evidence is a noted next step.

**Validation at the boundary** uses .NET 10's built-in minimal-API validation:
- description 1–2000 chars;
- amounts > 0, ≤ 10,000,000, at most 2 decimals;
- a reason is required for rejections, threshold changes and revisions;
- report range `from ≤ to` and ≤ 366 days;
- capped page size and body size.

The domain re-checks invariants. EF parameterises SQL. The client renders with `textContent` only, under a CSP of `default-src 'self'`.

**Secrets:**
- **Locally:** a throwaway `postgres/postgres` in `appsettings.Development.json`, overridable via user-secrets or env var. The Data Protection key ring lives in the user profile. Dev seed passwords are listed in the README, and seeding runs **only** in Development.
- **In production:**
  - the connection string comes from a secret manager (Key Vault / Secrets Manager) or a managed identity;
  - Data Protection keys are persisted to shared storage and encrypted with a KMS key;
  - there's no seeding.

---

## 9. Tests: what and why

Tests run against real Postgres. Each test creates its own orgs, so tests stay isolated without a cleanup library.

| Test | Why |
|---|---|
| **Approval rule table**: `newAmount` vs `threshold` vs `approvedAmount`, including equal-to-threshold, a decrease, an increase staying under, and an increase crossing | The core business rule. Boundaries are where it breaks. |
| **Transition table**: every `(state × revision kind × action)`, where legal ones succeed and the rest return 409 | The brief says "enforce that". Covers the whole table, not just happy paths. |
| **Completion blocked**: an actual cost over the authorised amount stays PendingApproval until approved; rejection returns it to Approved | Your explicit rule (answer 5). |
| **Permission handler**: a Requester can't decide; an Approver can't decide their own request or a revision they submitted; an OrgAdmin can't decide | The conflict-of-interest rules, tested at the permission layer where they live. |
| **Stale revisionId → 409; concurrent approve + reject → one wins** | Nobody approves an amount they didn't see. |
| **Threshold**: only OrgAdmin can change it; a change doesn't affect existing decisions (snapshot); it's audited | The self-approval bypass you identified. |
| **Cross-tenant by ID**: org B uses org A's request, revision and site IDs on every endpoint → 404, and org A's data never appears in B's report | The headline security requirement, tested the way an attacker would. |
| **Every tenant entity has a query filter; `IgnoreQueryFilters` only in login** | Guards against the *next* entity or query someone adds. |
| **Spend report numbers** against a hand-computed fixture: date boundaries, non-completed excluded, zero-spend sites included, other org excluded | "Returns the right numbers" is graded. |
| **Audit**: an auto-approval and a manual approval each write the expected events with the right actor; raw-SQL UPDATE/DELETE on `audit_events` fails | Compliance claims proven at the DB level. |
| **Timestamps**: `created_at` is set on insert, `updated_at` changes on update, `created_at` stays fixed | Your explicit requirement. Cheap to guard. |

**Not tested, deliberately:** framework behaviour (binding, per-field validation attributes, EF mapping trivia), the static client, and logging.

---

## 10. Decisions from review

| # | Question | Decision |
|---|---|---|
| 1 | Can Approvers raise requests? | **Yes.** |
| 2 | Do Requesters see only their own requests? | **No.** Everyone sees all requests in their org. |
| 3 | Who changes the threshold? | **API, OrgAdmin only.** Default 10,000, audited, snapshotted per revision. |
| 4 | Does equal-to-threshold need approval? | **Yes.** |
| 5 | Cost overrun? | **Needs new approval; completion is blocked until then.** It's the same rule as every other money change (§6). |
| 6 | Frontend? | **Plain HTML + JS.** |
| 7 | Who sees the spend report? | **Approvers only.** |
| — | How many approvals over threshold? | **Any one eligible Approver** (not the requester, not the revision submitter). |
| — | Where does "can't approve your own request" live? | **In the permission layer** (a resource-based authorization handler), not the entity. |

**Assumptions I made (please object if wrong):**
1. **OrgAdmin can't raise or approve requests** (separation of duties). Each user has one role.
2. **OrgAdmin can set the threshold in either direction.** Raising is the sensitive direction; both are audited.
3. **No edits or revisions while PendingApproval.** Approvers decide on content that can't change.
4. **A rejected estimate change keeps the old approved amount.** A rejected actual cost sends the request back to Approved to resubmit.
5. **Completing a request** (recording the actual cost) can be done by the requester or any Approver.
6. **One currency per org.** Report dates are UTC.

---

## 11. Execution order (commit + push per step)

1. **Domain:** entities, the approval rule, the transition table, plus unit tests. *I review the transition table by hand before tests are written from it.*
2. **Schema:** configs, timestamp interceptor, migration, composite FKs, CHECKs, indexes, audit trigger. *Generated migration SQL reviewed line by line.*
3. **Tenancy:** TenantContext, query filters, SaveChanges guard, plus meta-tests.
4. **Auth:** login, `/me`, revalidation, rate limit, role policies, `CanDecideRevision`, dev seed (2 orgs × sites × Requester / Approver / Approver2 / OrgAdmin).
5. **Request endpoints:** create, edit, revise, complete, approve, reject, history, plus HTTP authz and cross-tenant tests.
6. **Organization threshold endpoint** plus tests.
7. **Spend report** plus hand-computed fixture tests.
8. **Frontend:** static client.
9. **Docs:** DECISIONS / README / AI-LOG, then a timed clean-clone run.
