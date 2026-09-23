# Plan: Maintenance Request & Approval Backend

Status: **v3.2.** Steps 1–5 built. The schema changes from the step-1 review are applied in §3. Decisions are in §10. Nothing is built yet.

---

## 1. Scope

**Build:**
- A multi-tenant JSON API:
  - auth;
  - **permission-based roles** and **OrgAdmin user management**;
  - the request lifecycle with **revisions and re-approval**;
  - org threshold management;
  - the spend report;
  - a full per-request audit history.
- A minimal static HTML + JS client, including a small admin panel.

**Deliberately not building:**

| Not building | Why |
|---|---|
| Creating orgs and sites through the API | Out of the brief. Seeded. The facilities company onboards clients; tenants don't create tenants. |
| Self-registration, invite emails, password reset by email | Users are created by their OrgAdmin (§5). Email flows need infrastructure and don't change the security model being assessed. |
| Deleting users, roles or requests | They're referenced by the audit history. Users are **deactivated** instead. |
| Cancellation, attachments, comments, multi-currency | Not required. Each adds states or tables. |
| JWT, SSO | Cookie session (§5). |
| Postgres RLS, four-eyes on admin actions | Production hardening. Noted in §5 and §8. |

---

## 2. Architecture

A single ASP.NET Core project, organised by feature:

```
src/Assessment.Api/
  Domain/            MaintenanceRequest (state machine), RequestRevision, Organization, Site,
                     User, Role, Permission (catalog), AuditEvent
  Authorization/     permission policies + resource-based handlers (CanDecideRevision, CanModifyRequest)
  Features/
    Auth/            login, logout, me
    Requests/        create, list, get, edit, complete, approve, reject, history
    Admin/           users, roles & permissions, threshold      (OrgAdmin)
    Reports/         spend-by-site
  Infrastructure/
    Data/            AppDbContext, configs, migrations, seed, timestamp interceptor
    Tenancy/         TenantContext (request-scoped), query filters, SaveChanges guard
  wwwroot/           static HTML + vanilla JS client (requests + admin panel)
```

The work divides into three concerns:
- **Who may act** lives in `Authorization/`: permissions and conflict-of-interest rules.
- **What is legal from which state** lives in `Domain/`.
- **Which data exists for you at all** lives in `Tenancy/`.

Endpoints are thin: bind → validate → authorize → domain method → save. There's no MediatR, AutoMapper, repository layer or `Stateless`, and the reasoning hasn't changed from v2. The client calls only the API, so there's one entry point and one place where authorization happens.

---

## 3. Data model

Shared schema, with `organization_id` on every tenant-owned row. IDs are `Guid.CreateVersion7()`. **Every table has timestamps and user stamps:** `created_at`, `updated_at` (`timestamptz`, UTC) and `created_by_id`, `updated_by_id` (NULL = system/seed). A SaveChanges interceptor sets all four from an injected `TimeProvider` and the logged-in user in `TenantContext`, so no feature can forget them. The one exception is `audit_events`: it has `created_at` + `actor_user_id` only, because its rows can never change (§8).

```
organizations      id, name, approval_threshold numeric(12,2) NOT NULL DEFAULT 10000 CHECK >= 0

sites              id, organization_id, name
                   UNIQUE (organization_id, name), UNIQUE (id, organization_id)

roles              id, organization_id, name, is_system_admin bool,  -- the OrgAdmin role: locked, all permissions
                   permissions text[]                                 -- names from the fixed code catalog
                   UNIQUE (organization_id, name), UNIQUE (id, organization_id)

users              id, organization_id, role_id, email (UNIQUE on lower(email)), display_name,
                   password_hash, is_active
                   FK (role_id, organization_id) → roles(id, organization_id)
                   UNIQUE (id, organization_id)

maintenance_requests
                   id, organization_id, site_id, requested_by_id,
                   approval_threshold numeric(12,2)     -- org threshold snapshotted at creation; governs every revision of this request
                   status (Raised|PendingApproval|Approved|Rejected|Completed),
                   description, estimated_cost          -- current content (may be awaiting approval)
                   actual_cost NULL, completed_at NULL, xmin (concurrency token)
                   -- pending revision / last approved content are derived from request_revisions
                   FK (site_id, organization_id), FK (requested_by_id, organization_id)
                   CHECK status = 'Completed' ⇒ actual_cost, completed_at NOT NULL

request_revisions  -- an immutable snapshot of every change to what is being paid for
                   id, organization_id, request_id, sequence (1, 2, 3… per request),
                   kind (Initial|Edit|ActualCost), description, amount,
                   previous_revision_id NULL, reason NULL, submitted_by_id,
                   outcome (Pending|AutoApproved|Approved|Rejected|Superseded),
                   decided_by_id NULL, decided_at NULL, decision_comment NULL
                   FK (request_id, organization_id), FK (submitted_by_id, organization_id),
                   FK (decided_by_id, organization_id), FK (previous_revision_id, organization_id)
                   UNIQUE (request_id, sequence)
                   UNIQUE (request_id) WHERE outcome = 'Pending'   -- at most one pending revision, enforced by the DB
                   CHECK decided_by_id IS NULL OR decided_by_id <> submitted_by_id

audit_events       id bigint identity, organization_id, entity_type, entity_id, action,
                   from_status, to_status, actor_user_id NULL (NULL = system), details jsonb, created_at
                   TRIGGER: reject UPDATE / DELETE / TRUNCATE
```

**Why `request_revisions`:** each edit, cost change and actual cost becomes a full snapshot row. That gives you, per request, *what* was asked for, *who* asked, *who* approved or rejected it (or whether it was automatic), *against which threshold*, and what it replaced. The approval record is data, and it can be queried directly, not just read out of logs.

**Why the composite FKs:** the DB refuses a request, revision, user or role assignment that points at another org's site, user or role, even if application code has a bug.

**Indexes:**
- `maintenance_requests (organization_id, status, created_at DESC)`: the list and approval queue.
- `maintenance_requests (organization_id, site_id, completed_at) INCLUDE (actual_cost) WHERE status='Completed'`: the report.
- `request_revisions (request_id, created_at)`: revision history.
- `audit_events (organization_id, entity_id, created_at)`: request history.
- `users (lower(email))` unique: login.
- `users (organization_id, role_id)`: admin panel and approver lookup.

---

## 4. Tenant isolation

**How the org is established:**
1. First load has no session → 401 → the login form.
2. `POST /api/auth/login` checks the credentials and loads the user from the DB. The org ID and role come from **that user's DB record**.
3. It issues an HttpOnly cookie carrying `userId`, `orgId` and a security stamp.
4. The client calls `GET /api/me` for details. The org ID is never read from a URL, query string or body.

| Layer | Mechanism | What it catches |
|---|---|---|
| 1. Request scope | A request-scoped `TenantContext` is populated from the authenticated user. All data access goes through it. | Callers claiming another tenant. |
| 2. Reads | An **EF Core global query filter** on every tenant entity. Every query is org-locked automatically. | A forgotten `.Where`. A foreign ID doesn't exist → **404**. |
| 3. Writes | A SaveChanges guard **verifies** that every added, modified or deleted row belongs to the caller's org, and throws otherwise. It also refuses writes with no tenant, and any change to audit rows. It verifies rather than stamps, so a wrong org surfaces as a bug instead of being silently "fixed". | Code attaching foreign data. It also means an admin can only create users and roles **in their own org**, regardless of the request body. |
| 4. Database | Composite FKs. | Cross-tenant references from bugs in the layers above. |

The one allowed `IgnoreQueryFilters()` is login's email lookup, and a test enforces that. This is at the data layer because per-endpoint checks get forgotten, while a query filter is on by default.

---

## 5. Authentication, roles and permissions

**Session:**
- Cookie auth against our own `users` table. Passwords are hashed with `PasswordHasher<T>` (PBKDF2).
- The cookie is `HttpOnly`, `Secure` and `SameSite=Strict`, with an 8h sliding expiry.
- **Permissions are loaded from the DB on every request** (one indexed query), not baked into the cookie. Deactivating a user, changing their role or editing a role's permissions takes effect on the **next request**.
- CSRF: SameSite=Strict, JSON-only mutations, no CORS.
- Login: a generic error message and a rate limit.
- **Rejected:** permissions carried in the cookie or a JWT, because a revoked approver would keep approving until the token expired.

**Model:** each user has **one role**, a role has a **set of permissions**, and all checks are made against **permissions, never role names**.

| Permission | Allows | Requester | Approver | OrgAdmin |
|---|---|---|---|---|
| `requests.create` | raise requests; edit or complete **own** requests | ✅ | ✅ | ✅ |
| `requests.manage` | edit or complete **any** request in the org | | ✅ | ✅ |
| `requests.approve` | approve or reject pending revisions | | ✅ | ✅ |
| `reports.spend` | spend report | | ✅ | ✅ |
| `admin.users` | create users, set their role, deactivate, reset password | | | ✅ *(locked)* |
| `admin.roles` | create roles, edit the permissions of non-admin roles | | | ✅ *(locked)* |
| `admin.settings` | change the approval threshold | | | ✅ *(locked)* |

- Viewing requests and their history needs no permission beyond being in the org (decision 2).
- **Each org is seeded with these three roles.** In the admin panel, the OrgAdmin adds users (name, email, initial password, role) and can edit or create other roles.
- `admin.*` permissions **can't be granted to other roles**, and the OrgAdmin role can't be edited. This avoids accidentally creating a second admin through a role edit.

**Resource-based handlers** (these rules need the record loaded; failing one returns 403):
- **`CanDecideRevision`:** the caller has `requests.approve`, **isn't the request's creator**, and **isn't the submitter of the revision**. This applies to OrgAdmins too: all permissions never includes approving your own request.
- **`CanModifyRequest`:** the caller has `requests.manage`, or has `requests.create` and is the request's creator.

**Admin guardrails:**
- An admin can't deactivate themselves, change their own role, or remove the org's **last** active OrgAdmin.
- Passwords must be at least 12 characters. They're hashed and never returned or logged.
- Emails are globally unique, so login doesn't need an org code. *Trade-off:* an admin adding an email that already exists in another org learns that the email is in use somewhere. I accept this as low severity; the alternative is making every user type an org code at login.

**OrgAdmin power: how it's contained (decision 11).**
- **No one approves their own request.** `CanDecideRevision` applies to every user, the OrgAdmin included. Holding `requests.approve` never covers your own request or a revision you submitted.
- **A threshold change applies only to requests created after it.** Each request snapshots the threshold at creation, and all its edits and its actual cost are judged against that snapshot. Raising the threshold can't turn an existing pending request, or an overrun on an existing job, into an auto-approval. Lowering it doesn't retroactively add approval steps either.
- **Everything is user-stamped.** The threshold change, the request's creation, every update and every approval record *who* and *when*, both in the audit trail and in each row's `created_by_id` / `updated_by_id`. Every user row also records which admin created that account.

**Residual risk, accepted.** An OrgAdmin can still (a) raise the threshold and *then* create a request that's auto-approved under the new value, or (b) create a second account that approves their requests. Neither is hidden: the audit shows "X raised the threshold at T1, X raised a request at T2, auto-approved", and the approving account carries `created_by_id = X`. The control is **transparency, not prevention**, and that's a product decision. *Production option, noted in DECISIONS.md:* four-eyes confirmation by a second OrgAdmin for threshold increases and new approver accounts.

---

## 6. Request lifecycle and approval rules

**Every change is a revision.** Creating a request, editing its description or estimate, and entering the actual cost each produce a `request_revisions` row. Each revision is checked against the **request's own threshold**: the org threshold snapshotted when the request was created. Later threshold changes affect only requests created after them.

**Two approval rules:**

```
Initial / Edit revision:
  needsApproval = amount >= threshold
                  AND (amount > approvedAmount OR description changed from the approved revision)

ActualCost revision:
  needsApproval = actualCost >= threshold
```

- An **initial** request has no approved amount yet, so it needs approval exactly when `amount >= threshold`. Equal to the threshold requires approval.
- **An edit to a request at or above the threshold** needs re-approval if the cost goes up or the description changes. The Approver approved specific content, and different content needs a new decision.
- **Under the threshold,** edits are auto-approved and recorded. **Cost decreases** with an unchanged description are also auto-approved: less spend can't be a bypass.
- **Actual cost:** if it's **at or above the threshold**, it always needs approval before the request can be Completed (decision 5). In practice, an over-threshold job gets two sign-offs: the estimate before the work, and the actual cost after it.
- **Any one eligible Approver** decides.

**Editing while pending (decision 3):**
- An edit during PendingApproval **supersedes** the pending revision (`outcome = Superseded`, audited).
- The new revision is evaluated against the last *approved* revision, so the request needs re-approval.
- Approvers must send the `revisionId` they reviewed. Approving a superseded revision returns **409**, so nobody approves content they didn't see.

**Rejection** falls back to the last approved content:
- **With a previous approval** (a rejected edit): the request goes back to **Approved** with that revision's description and amount.
- **Rejected actual cost:** back to **Approved** so the actual can be resubmitted (decision 4).
- **With nothing approved yet** (a rejected initial request): **Rejected**, which is terminal.

| From | Action | Condition | To |
|---|---|---|---|
| — | create | — | Raised (transient, audited) |
| Raised | route (system) | no approval needed | Approved (revision AutoApproved) |
| Raised | route (system) | needs approval | PendingApproval |
| PendingApproval (Initial/Edit) | edit | — | old revision Superseded; new one routed as above → Approved or PendingApproval |
| PendingApproval (Initial/Edit) | approve | CanDecideRevision, revisionId current | Approved |
| PendingApproval (Initial/Edit) | reject | CanDecideRevision, revisionId current | Approved (previous content), or **Rejected** if nothing was ever approved |
| Approved | edit | no approval needed / needs approval | Approved / PendingApproval |
| Approved | complete (actual) | actual < threshold | **Completed** |
| Approved | complete (actual) | actual ≥ threshold | PendingApproval (ActualCost): completion blocked |
| PendingApproval (ActualCost) | resubmit actual | — | old Superseded; new one routed |
| PendingApproval (ActualCost) | approve / reject | CanDecideRevision, revisionId current | **Completed** / Approved (actual cleared) |
| Rejected, Completed | anything | — | **409** (terminal) |

- While an actual cost is pending, only resubmitting the actual is allowed. The work is done, so editing the description or estimate then doesn't mean anything.
- Concurrent writers are caught by `xmin` (409).
- With no eligible Approver, the request **stays pending**. It's never auto-approved.

---

## 7. API

| Method | Route | Permission | Notes |
|---|---|---|---|
| POST | `/api/auth/login` · `/logout` | — | login returns user + org + permissions |
| GET | `/api/me` | authenticated | user, role, permissions, org, threshold |
| GET | `/api/sites` | authenticated | |
| GET | `/api/requests?status=&siteId=&page=` | authenticated | whole org, paged |
| POST | `/api/requests` | `requests.create` | `{ siteId, description, estimatedCost }` |
| GET | `/api/requests/{id}` | authenticated | includes current + pending revision |
| PUT | `/api/requests/{id}` | CanModifyRequest | `{ description, estimatedCost, reason }` → new revision |
| POST | `/api/requests/{id}/complete` | CanModifyRequest | `{ actualCost, reason? }` |
| POST | `/api/requests/{id}/approve` | CanDecideRevision | `{ revisionId, comment? }` |
| POST | `/api/requests/{id}/reject` | CanDecideRevision | `{ revisionId, reason }` |
| GET | `/api/requests/{id}/history` | authenticated | revisions + audit events |
| GET | `/api/reports/spend?from=&to=` | `reports.spend` | |
| GET / POST | `/api/admin/users` | `admin.users` | create: `{ displayName, email, password, roleId }` |
| PUT | `/api/admin/users/{id}` | `admin.users` | role, active, password reset |
| GET / POST | `/api/admin/roles` | `admin.roles` | create role with permissions |
| PUT | `/api/admin/roles/{id}/permissions` | `admin.roles` | non-admin roles only |
| GET | `/api/admin/permissions` | `admin.roles` | the catalog |
| PUT | `/api/admin/settings/threshold` | `admin.settings` | `{ amount, reason }` |

**Spend report:**
- Sum of `actual_cost` for **Completed** requests, by `completed_at`.
- Inclusive UTC dates.
- **All** org sites are returned, including zero-spend ones: `siteId, siteName, totalSpend, completedCount`.
- One grouped SQL query.

**Errors:** `ProblemDetails`. 400 · 401 · 403 · 404 (missing or foreign) · 409 (illegal transition, stale revision, concurrency, admin guardrail).

---

## 8. Audit trail, validation, secrets

**Audit.** Every state change, decision and admin action writes an `audit_events` row **in the same transaction** as the change, produced by domain or feature code. It's never optional.

| Event | Actor | Details |
|---|---|---|
| `Raised` | requester | site, description, estimate |
| `AutoApproved` | **system** | revision id, amount, threshold snapshot, `triggeredBy` |
| `SubmittedForApproval` | submitter | revision id, kind, amount, threshold |
| `Edited` | editor | before → after (description, estimate), reason |
| `RevisionSuperseded` | editor | old → new revision id |
| `Approved` / `Rejected` | approver | revision id, amount, comment / reason |
| `ActualCostSubmitted` · `Completed` | submitter | actual, threshold |
| `ThresholdChanged` | OrgAdmin | old → new, reason (either direction) |
| `UserCreated` · `UserRoleChanged` · `UserDeactivated` · `UserReactivated` · `PasswordReset` | OrgAdmin | target user, before → after (never the password) |
| `RoleCreated` · `RolePermissionsChanged` | OrgAdmin | role, permissions before → after |

**Who can modify the audit trail:**
- **Through the app, nobody.** There's no update/delete path or endpoint.
- **At the DB:** a trigger rejects UPDATE, DELETE and TRUNCATE, even from the app's own DB user. A raw-SQL test proves this.
- **In production:** the app's DB role has only `INSERT, SELECT` on `audit_events`, and a separate role owns it. Events are shipped to WORM storage. A per-org hash chain is a noted next step.
- **Not even an OrgAdmin** can touch the audit trail. They can only add events by acting.

**Validation at the boundary** uses .NET 10's built-in minimal-API validation:
- description 1–2000 chars;
- amounts > 0, ≤ 10,000,000, at most 2 decimals;
- a reason is required for rejections, edits and threshold changes;
- email format, password ≥ 12, display name ≤ 200;
- permission names must be in the catalog;
- report range ≤ 366 days, `from ≤ to`;
- capped page size and body size.

The domain re-checks invariants. SQL is parameterised. The client renders with `textContent` only, under a CSP of `default-src 'self'`.

**Secrets:**
- **Locally:** a throwaway `postgres/postgres` in `appsettings.Development.json`, overridable via user-secrets or env var. Dev seed passwords are in the README, and seeding runs only in Development.
- **In production:**
  - the connection string comes from Key Vault / Secrets Manager or a managed identity;
  - Data Protection keys are persisted to shared storage and KMS-encrypted;
  - there's no seed;
  - users get invite links rather than admin-set passwords.

---

## 9. Tests: what and why

Tests run against real Postgres, and each test creates its own orgs.

| Test | Why |
|---|---|
| **Approval rules**: amount vs threshold vs approved amount, description changed or not, equal-to-threshold, decrease, actual below / equal / above the threshold | The core business rules. Every boundary. |
| **Transition table**: every `(state × revision kind × action)`, where legal ones succeed and the rest return 409 | The brief says "enforce that". Covers the whole table. |
| **Edit while pending** supersedes; approving the old `revisionId` → 409; rejecting falls back to the last approved content | Decision 3, and the "approve what you saw" guarantee. |
| **Completion blocked** when actual ≥ threshold until approved; a rejection returns the request to Approved | Decisions 4 and 5. |
| **Permission handlers**: no `requests.approve` → 403; an approver's own request → 403; a revision they submitted → 403; **an OrgAdmin's own request → 403** | Conflict of interest, including for the all-permissions role. |
| **Permission changes take effect on the next request**: revoke `requests.approve` from a role, then that approver's next call → 403 | Proves permissions aren't cached in the cookie. |
| **Admin guardrails**: can't grant `admin.*` to another role, can't edit the OrgAdmin role, can't deactivate the last admin or yourself; a created user always lands in the admin's own org, whatever the body says | Where privilege escalation would happen. |
| **Cross-tenant by ID**: org B uses org A's request, revision, site, user and role IDs on every endpoint → 404, including an admin assigning another org's role | The headline security requirement. |
| **Every tenant entity has a query filter; `IgnoreQueryFilters` only in login** | Guards the next entity or query someone adds. |
| **Spend report** against a hand-computed fixture: date boundaries, non-completed excluded, zero-spend sites included, other org excluded | "Right numbers" is graded. |
| **Audit**: auto and manual approvals, supersede, and admin actions each write the expected event and actor; raw-SQL UPDATE/DELETE on `audit_events` fails | Compliance claims, proven at the DB level. |
| **Threshold snapshot**: raise the threshold, then (a) an existing request's edit/actual is still judged by its old threshold, and (b) a new request uses the new one | Decision 11. It's what stops a threshold change from rewriting pending requests. |
| **Timestamps + user stamps**: `created_at`/`created_by_id` set once; `updated_at`/`updated_by_id` move on change and name the acting user | Explicit requirement. |

**Not tested, deliberately:** framework behaviour, per-field validation attributes, the static client, logging.

---

## 10. Decisions from review

| # | Topic | Decision |
|---|---|---|
| 1 | Roles | **One role per user; roles carry permissions; checks use permissions.** OrgAdmin has all permissions, including raising and approving (never their own), and manages users and roles from an admin panel. |
| 2 | Visibility | Everyone sees all requests in their org. |
| 3 | Threshold | Default 10,000. OrgAdmin changes it through the API in either direction; audited. |
| 4 | Equal to threshold | Needs approval. |
| 5 | Edits while pending | Allowed. They supersede the pending revision and need re-approval (if at/above the threshold). |
| 6 | Rejection | A rejected edit keeps the last approved content; a rejected actual goes back to Approved; a rejected initial request is terminal. |
| 7 | Actual cost | Needs approval if ≥ threshold. Completion is blocked until then. Requester or `requests.manage` can submit it. |
| 8 | Approvals needed | Any one eligible Approver. |
| 9 | Self-approval | A permission-layer rule (resource-based handler), and it applies to OrgAdmin too. |
| 10 | Frontend / report | Plain HTML + JS; spend report needs `reports.spend`. |
| 11 | Threshold scope | **A new threshold applies only to requests created after the change.** Each request snapshots it at creation. |
| 12 | User stamps | `created_by_id` / `updated_by_id` on every table, plus the actor on every audit event. The remaining OrgAdmin risk is handled by transparency (§5). |

**Assumptions (accepted in review, no objections raised):**
1. **`admin.*` permissions are locked to the OrgAdmin role.** Other roles can be created or edited, but never with admin permissions.
2. **Requesters edit and complete only their own requests;** `requests.manage` holders can do it for any request.
3. **Under the threshold, a description-only edit is auto-approved** (just recorded). At or above it, any description change needs re-approval.
4. **An OrgAdmin can't approve their own request.** Now confirmed as decision 11.
5. **Orgs and sites are seeded, with no API.** Each seeded org has an OrgAdmin.

---

## 11. Execution order (commit + push per step)

1. **Domain:** entities, the permission catalog, the approval rules, the transition table, plus unit tests. *I review the transition table by hand before tests are generated from it.*
2. **Schema:** configs, timestamp + user-stamp interceptor, migration, composite FKs, CHECKs, indexes, audit trigger. *Migration SQL reviewed line by line.*
3. **Tenancy:** TenantContext, query filters, SaveChanges guard, plus meta-tests.
4. **Auth and permissions:** login, `/me`, per-request permission loading, policies, `CanDecideRevision` / `CanModifyRequest`, dev seed (2 orgs × sites × OrgAdmin / Approver ×2 / Requester).
5. **Request endpoints**, plus HTTP authz and cross-tenant tests.
6. **Admin endpoints** (users, roles, threshold), plus guardrail tests.
7. **Spend report**, plus hand-computed fixture tests.
8. **Frontend:** requests pages and admin panel.
9. **Docs:** DECISIONS / README / AI-LOG, then a timed clean-clone run.
