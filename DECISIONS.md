# Decisions

One page. The full table, with every alternative and trade-off, is in [docs/decisions-full.md](docs/decisions-full.md). The design is in [PLAN.md](PLAN.md), [ARCHITECTURE.md](ARCHITECTURE.md) and [SCHEMA.md](SCHEMA.md).

## Choices

| Area | Chose | Rejected | Why / trade-off |
|---|---|---|---|
| Platform | .NET 10 LTS, minimal APIs, **one project organised by feature** | Clean Architecture's 4 assemblies; MVC; MediatR/AutoMapper | Proportionate. The boundaries are folders: `Domain` (state rules), `Authorization` (who), `Tenancy` (which data). |
| Data | **PostgreSQL + EF Core** (snake_case), migrations; startup migrations **serialised with an advisory lock** | SQLite, SQL Server, Dapper; unguarded `MigrateAsync` | Production-like behaviour, and constraints the DB enforces itself. The lock stops parallel hosts racing on a fresh database. |
| Indexing & paging | **Every index serves a known query** and was checked with `EXPLAIN` on EF's real SQL. The request list's two indexes end in its exact sort (`created_at DESC, id DESC`), so pages need no sort. Offset paging, capped at 100 per page | Keyset (cursor) paging; hand-pruning EF's FK indexes | Offset paging is simple and fine at this scale; keyset is the upgrade for very deep pages. |
| Tenancy | **Shared schema, `organization_id` everywhere**, in 4 layers: org from the user's DB record → **global query filters** (404 for foreign IDs, fail-closed with no tenant) → **SaveChanges guard** (verifies, doesn't stamp) → **composite FKs** `(x_id, organization_id)` | Per-endpoint checks; schema-per-tenant; Postgres RLS (next hardening step) | Isolation is on by default. The only bypass is the login email lookup, and a test enforces that. |
| Auth | **HttpOnly SameSite=Strict cookie**; user and permissions **reloaded every request**; the session stamp comes from the password hash; **identical 401** for every login failure; **per-IP rate limit** | JWT; full ASP.NET Identity; account lockout | No token for XSS to steal; revocation is immediate; no account enumeration. Lockout would let anyone lock out a known user. |
| Permissions | One role per user, **roles carry permissions**; checks use permissions; **self-approval is a resource-based authorization rule** that applies to OrgAdmin too; DB CHECK as backstop | Role-name checks; the rule inside the entity | "Who may act" is authorization. `admin.*` is locked to OrgAdmin. |
| Endpoints | One path for every mutation: load via the tenant filter (**404**) → authorize on the record (**403**) → domain (400/409) → save (409) | Per-endpoint ordering | 404 before 403, so a 403 never confirms another tenant's record exists. |
| Validation | .NET 10 built-in minimal-API validation at the boundary; **the domain re-checks invariants**; malformed input → 400, never 500 | FluentValidation | No extra dependency. The domain is the authority, so no entry point can skip it. |
| Lifecycle | A hand-written **transition table**, tested over every state × action | `Stateless` library | 5 states; exhaustive tests beat a dependency. |
| Money changes | Every change is an immutable **revision**. Approval is needed if `amount ≥ the request's threshold` and (it went up **or** the wording changed). An actual cost `≥ threshold` blocks completion. Approvers must send the `revisionId` they saw. | Approval columns on the request; flag-and-acknowledge overruns | Approval history is queryable data, and nobody approves content they didn't see. |
| Threshold | **Snapshotted per request at creation**; changed only by OrgAdmin, audited | Evaluating against the current value | A change never rewrites how existing requests are treated. |
| Concurrency | `xmin` on the request row; **revision changes also touch the row** | Pessimistic locks | Two approvers at once: one wins, the other gets 409. Forced-interleaving tests. |
| Audit | Written **in the same transaction** by the domain; **DB trigger blocks UPDATE/DELETE/TRUNCATE**; the app guard refuses changes; org-wide log for admins | App-only convention | Tested with raw SQL. Production: an INSERT/SELECT-only DB role plus WORM export. |
| Report | `SUM(actual_cost)` of Completed requests by `completed_at`; **inclusive UTC dates as `[from, to+1)`**; every site, zeros included; one SQL statement | Counting estimates; closed ranges | Hand-computed fixture tests. `EXPLAIN` shows an Index Only Scan on the partial covering index. |
| Password fields | **Encrypted in the payload** (RSA-OAEP-SHA256 over a single-use nonce + the password) for login and admin forms; **plus HTTPS + HSTS** outside Development | HTTPS alone (the agent's recommendation) | Product decision: captured payloads don't reveal reusable passwords and can't be replayed. It doesn't defend against script in the page. |
| Secrets | **Locally:** throwaway DB credentials in `appsettings.Development.json`, overridable via user-secrets; an in-memory RSA key. **Production:** connection string and RSA key from a secret manager (built: startup fails without the key); Data Protection keys persisted and KMS-encrypted (deployment work, not in this repo) | Secrets in config files; keys on disk | Nothing sensitive in git. Production misconfiguration fails at startup, not at the first login. |
| Client | Static HTML + vanilla JS on the same origin; `textContent` only; **strict CSP** | Razor Pages; a SPA framework | One entry point, so one place for authorization. No build step. |
| Tests | Real Postgres via `WebApplicationFactory`; fresh orgs per test; **every suite checked with planted bugs**; a headless-browser smoke test | EF InMemory; Testcontainers (no Docker here) | InMemory skips constraints and SQL. The planted bugs found a gap the tests hid (AI-LOG). |

## Deliberately not built

- **Self-registration and organization management.** Tenants don't create tenants; seeded. Any member can **add** a site (it always lands in their own org); renaming and deleting sites aren't built, because requests reference them.
- **Deleting users, roles or requests.** The audit history references them; users are deactivated.
- **A "last active OrgAdmin" guard.** It can never trigger: only OrgAdmins manage users, and they can't deactivate or demote themselves.
- **Request cancellation, attachments, comments.** Not required; each adds states.
- **Four-eyes for admin actions, RLS, hash-chained audit, multi-currency, org timezones.** These are documented next steps.

## Accepted risk

An OrgAdmin can raise the threshold and then raise an auto-approved request, or create a second approver account. Neither is prevented: every step is user-stamped and in the org audit log, and `createdBy` is shown per user. The control is **transparency** (a product decision).

## Assumptions made

- "Clean machine" = the .NET 10 SDK plus a **local PostgreSQL** (the tested setup; Docker Compose is an optional, untested alternative). Node is only needed for the CLI login and browser test.
- Equal to the threshold needs approval. One currency per org. Report dates are UTC.
- Emails are globally unique, so login needs no org code; an admin can learn an email exists elsewhere (low severity).
- Requesters edit and complete their own requests; `requests.manage` covers any request. Below the threshold, a description-only edit is auto-approved.
- Any **one** eligible approver decides; with none eligible, a request stays pending (never auto-approved).
