# Maintenance Requests & Approvals

Backend (with a minimal UI) for a facilities management company. Site staff raise maintenance requests, requests cost money, and money needs approval before work proceeds. Features:

- multi-tenant organisations, strictly isolated;
- permission-based roles;
- threshold-based approval, with re-approval when costs change;
- a spend report;
- a tamper-resistant audit trail.

ASP.NET Core (.NET 10) · EF Core · PostgreSQL · vanilla JS client.

| Doc | What's in it |
|---|---|
| [DECISIONS.md](DECISIONS.md) | One page: choices, what was rejected, trade-offs, what wasn't built, assumptions ([full record](docs/decisions-full.md)) |
| [AI-LOG.md](AI-LOG.md) | One page: what was delegated, a real prompt, the plausible-but-wrong instance, how output was checked ([full record](docs/ai-log-full.md)) |
| [CLAUDE.md](CLAUDE.md) | Agent configuration and rules |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Layers, request flow, where each guarantee is enforced (diagrams) |
| [SCHEMA.md](SCHEMA.md) | ER diagram, composite FKs, constraints, indexes and the query each serves |
| [PLAN.md](PLAN.md) | The design as agreed before coding, revised through three review rounds |

## Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | any .NET 10 SDK, 10.0.100 or later (`global.json` rolls forward to the newest installed) | `dotnet --list-sdks` |
| PostgreSQL | 14+, **either** Docker **or** a local install | `docker --version` / `psql --version` |
| Node.js *(optional)* | 18+, only for command-line login (`scripts/login.mjs`) and the browser smoke test | `node --version` |

## Run it

```bash
git clone https://github.com/MoqeetUlHassan/Assessment.git && cd Assessment

# 1. Start Postgres (skip if you already have one on localhost:5432 with user/password postgres/postgres)
docker compose up -d db

# 2. Run the app. In Development it creates the database, applies migrations and seeds demo data on startup.
dotnet run --project src/Assessment.Api --launch-profile http
```

Open **http://localhost:5183** and sign in with a seeded account (below).
- **Everyone:** the request list (filter, paging), raising a request (pick **+ Add a new site…** in the Site list to add one), and a detail page with approve/reject, edit, record the actual cost, revisions and the audit trail.
- **Approvers and admins:** also **Spend report**.
- **`admin@acme.test`:** also **Admin**: threshold, users, roles and permissions, and the organization audit log.

Other endpoints: `curl http://localhost:5183/health` (→ `Healthy`), and the OpenAPI document (Development only) at http://localhost:5183/openapi/v1.json.

**How long it takes (measured).** A timed run from a fresh `git clone`, with an **empty NuGet cache** (every package downloaded) and a **brand-new empty database**:
- clone: 3 s;
- restore and build: 44 s;
- first start, including migrations and seeding: healthy at **51 s**;
- **first successful login at 52 s**;
- the full test suite on another brand-new database: all green (re-checked on a brand-new database after the latest change: **146/146**).

On a truly clean machine, add the .NET 10 SDK install (about 3–5 minutes), for **well under 10 minutes in total**.

The Docker Compose path hasn't been exercised: the author's machine has no Docker and uses a local Postgres 16. It's a stock `postgres:16-alpine` service with the same credentials as `appsettings.Development.json`.

### Seeded accounts (Development only)

On first start in Development, two organizations are created so tenant isolation can be tried by hand. **Every account's password is `ChangeMe-Dev-2026!`**

| Organization | Email | Role | Can |
|---|---|---|---|
| Acme Retail | `admin@acme.test` | OrgAdmin | everything, incl. users, roles, threshold (never approve own requests) |
| Acme Retail | `approver1@acme.test`, `approver2@acme.test` | Approver | raise, edit any, approve/reject, spend report |
| Acme Retail | `requester@acme.test` | Requester | raise, edit/complete own |
| Globex Offices | `admin@globex.test`, `approver1@globex.test`, `approver2@globex.test`, `requester@globex.test` | same roles | the same, isolated from Acme |

- **Sites:** Acme has *Site 12, Downtown Store, Warehouse North*; Globex has *HQ Tower, Site 12, Data Centre*. Both having a "Site 12" is deliberate: names don't cross tenants.
- **Adding sites:** anyone signed in can add a site from the request form (**+ Add a new site…** at the bottom of the Site list). It always belongs to the adder's organization. Names are unique within an organization, ignoring case. Each addition appears in the admin audit log as *Site Created*. Sites can't be renamed or deleted, because requests and the spend report refer to them.
- **Demo requests:** a fresh database also gets a few requests in different states, created through the real domain methods with genuine audit trails, so the list and this month's spend report aren't empty.
- **When seeding runs:** only in Development (`Seed:DevelopmentData`), and only on a database that hasn't been seeded yet.

### Try the API with curl

Every password field (login, and the admin create-user and reset-password forms) is accepted **only encrypted** (see [Security](#security-where-its-enforced-and-how-its-verified)). A plain curl call can't log in. `scripts/login.mjs` does what the browser does and saves the session cookie to a curl cookie jar:

```bash
node scripts/login.mjs approver1@acme.test 'ChangeMe-Dev-2026!' jar.txt   # challenge → encrypt → login → jar.txt
curl -b jar.txt http://localhost:5183/api/me                              # who am I, which org, which permissions
curl -b jar.txt -X POST http://localhost:5183/api/auth/logout
```

A full request lifecycle (the requester raises 15,000 against a 10,000 threshold → an approver approves → actual cost → approved → Completed):

```bash
B=http://localhost:5183; H='Content-Type: application/json'; PW='ChangeMe-Dev-2026!'
node scripts/login.mjs requester@acme.test "$PW" req.jar
node scripts/login.mjs approver1@acme.test "$PW" app.jar

curl -s -b req.jar $B/api/sites                                   # pick a siteId
curl -s -b req.jar -H "$H" -d '{"siteId":"<siteId>","description":"Replace chiller","estimatedCost":15000}' $B/api/requests
#   → status PendingApproval, pendingRevision.id = <revisionId>
curl -s -b app.jar -H "$H" -d '{"revisionId":"<revisionId>","comment":"ok"}' $B/api/requests/<id>/approve
curl -s -b req.jar -H "$H" -d '{"actualCost":12000}' $B/api/requests/<id>/complete    # ≥ threshold → pending again
curl -s -b app.jar -H "$H" -d '{"revisionId":"<newRevisionId>"}' $B/api/requests/<id>/approve   # → Completed
curl -s -b req.jar $B/api/requests/<id>/history                   # who did what, when
```

### API summary

| Method | Route | Needs | Notes |
|---|---|---|---|
| GET | `/api/auth/password-challenge` | — | `{ keyId, publicKey, nonce }` for encrypting a password field (rate-limited) |
| POST | `/api/auth/login` · `/api/auth/logout` | — | login body `{ email, keyId, encryptedPassword }`; rate-limited per IP |
| GET | `/api/me` | session | user, role, permissions, org, threshold |
| GET | `/api/sites` | session | your org's sites |
| POST | `/api/sites` | session (any member) | `{ name }`; always created in **your** organization; unique per org (case-insensitive); audited |
| GET | `/api/requests?status=&siteId=&page=&pageSize=` | session | whole org, newest first, paged (≤ 100) |
| POST | `/api/requests` | `requests.create` | `{ siteId, description, estimatedCost }` |
| GET | `/api/requests/{id}` | session | includes revisions and the `actions` the caller may take |
| PUT | `/api/requests/{id}` | own request, or `requests.manage` | `{ description, estimatedCost, reason }` |
| POST | `/api/requests/{id}/complete` | own request, or `requests.manage` | `{ actualCost, reason? }` |
| POST | `/api/requests/{id}/approve` | `requests.approve`, not your request/revision | `{ revisionId, comment? }` |
| POST | `/api/requests/{id}/reject` | `requests.approve`, not your request/revision | `{ revisionId, reason }` |
| GET | `/api/requests/{id}/history` | session | revisions + audit events with actor names |
| GET | `/api/reports/spend?from=yyyy-MM-dd&to=yyyy-MM-dd` | `reports.spend` | spend per site, caller's org only; inclusive UTC dates, ≤ 366 days |
| GET / POST | `/api/admin/users` | `admin.users` | list / create `{ displayName, email, keyId, encryptedPassword, roleId }` |
| PUT | `/api/admin/users/{id}/role` | `admin.users` | `{ roleId }`; not yourself |
| POST | `/api/admin/users/{id}/deactivate` · `/reactivate` | `admin.users` | not yourself; ends their sessions |
| POST | `/api/admin/users/{id}/password` | `admin.users` | `{ keyId, encryptedPassword }` (≥ 12 after decryption); ends their sessions |
| GET / POST | `/api/admin/roles` | `admin.roles` | list / create `{ name, permissions[] }` |
| PUT | `/api/admin/roles/{id}/permissions` | `admin.roles` | `admin.*` can't be granted; the OrgAdmin role is locked |
| GET | `/api/admin/permissions` | `admin.roles` | the permission catalog |
| PUT | `/api/admin/settings/threshold` | `admin.settings` | `{ amount, reason }`; applies to new requests only |
| GET | `/api/admin/audit?entityType=&page=&pageSize=` | `admin.users` | organization-wide audit log |

Errors are `application/problem+json`:

| Status | When |
|---|---|
| 400 | Invalid or malformed input, or an invalid/expired password challenge |
| 401 | No session |
| 403 | Missing permission, or your own request / revision |
| 404 | Doesn't exist, **or belongs to another organization** (indistinguishable) |
| 409 | Illegal transition, a stale `revisionId`, a duplicate email, or a concurrent change |
| 429 | Too many login attempts or challenges from one IP |

## Security: where it's enforced and how it's verified

| Requirement | Enforced at | Proven by |
|---|---|---|
| **Tenant isolation**, including manipulated IDs | The org comes only from the user's DB record at login → **global query filters** on every tenant table (a foreign ID is a 404; no tenant means no rows) → **SaveChanges guard** (refuses writing another org's rows) → **composite FKs** in the DB | `Persistence/TenantIsolationTests` (real foreign IDs, a smuggled entity), `TenantModelConventionTests` (every entity filtered; `IgnoreQueryFilters` only in login), `Http/RequestAuthorizationHttpTests` (an OrgAdmin of another org gets 404 on every endpoint), `AdminHttpTests`, `SpendReportTests`, `SiteTests` (a site created with another org's ID in the body still lands in the caller's org, and the other org can't see or use it), `SchemaGuaranteesTests` (the FK rejects a cross-org site) |
| **Authorization, server-side** | Permission policies on every endpoint; resource handler `CanDecide` / `CanModify` (no approving your own request or your own change, OrgAdmin included); DB CHECK as backstop | `Authorization/RequestAuthorizationTests`, `Http/RequestAuthorizationHttpTests` (direct API calls, no UI), `AdminHttpTests` (403 on every admin endpoint) |
| **Input validation** | .NET 10 minimal-API validation on every body; the domain re-checks invariants (amount > 0, ≤ 10M, 2 dp; lengths; required reasons); malformed JSON → 400, never 500 | `Http/RequestLifecycleHttpTests.Malformed_input_is_a_400_never_a_500`, `SpendReportTests.Invalid_ranges_are_rejected`, `Domain/RevisionLifecycleTests` |
| **Sessions and passwords** | HttpOnly SameSite=Strict cookie; the user is reloaded every request (deactivation, reset and permission changes take effect immediately); identical 401 for every login failure; per-IP rate limit; **password fields encrypted in the payload** (single-use nonce); HTTPS + HSTS outside Development; strict CSP | `Http/AuthenticationTests`, `EncryptedLoginTests`, `ProductionModeTests`, `StaticClientTests`, `tests/e2e/browser-smoke.js` (no password in any real request body; injected HTML renders as text) |
| **Audit integrity** | Written in the **same transaction** as the change, by the domain; **DB trigger rejects UPDATE/DELETE/TRUNCATE**; the app guard refuses changes even in system scope | `Persistence/SchemaGuaranteesTests` (raw SQL tampering fails; a failed save leaves no audit row), `TenantIsolationTests`, `Domain/AuditTrailTests` (replaying the trail reproduces the status), `AdminHttpTests` (every admin action attributed, no passwords in details) |
| **Secrets** | See below | `ProductionModeTests` (startup fails without the key), `StartupConfigurationTests` (missing connection string fails fast) |

**Secrets: local vs production**

| Secret | Locally (what this repo does) | Production (what I'd do) |
|---|---|---|
| DB connection string | Throwaway `postgres/postgres` in `appsettings.Development.json`; override with user-secrets or env var | From a secret manager (Key Vault / Secrets Manager) or a managed identity; nothing in config files |
| Password-encryption private key | Generated in memory at each start, never on disk | `LoginEncryption__PrivateKeyPem` from the secret manager; startup refuses to run without it |
| Cookie-signing keys (Data Protection) | ASP.NET default key ring in the user profile | Persisted to shared storage and encrypted with a KMS key, so all instances share it |
| Seed accounts | Documented dev password; seeding only in Development | No seeding; real users are provisioned by an OrgAdmin |

Nothing sensitive is committed. The only credentials in the repo are the throwaway local ones above.

## Tests: what and why

146 xUnit tests run against **real PostgreSQL**; the EF InMemory provider would skip constraints, triggers and real SQL. Each test creates its own organizations, so no cleanup is needed. **Every suite was checked by planting bugs** and confirming the targeted tests fail (29 planted bugs; see AI-LOG).

| Suite | Why these tests |
|---|---|
| `Domain/TransitionTableTests` | "Enforce that" illegal transitions fail: **every** state × action pair, not just the happy paths |
| `Domain/ApprovalRulesTests`, `RevisionLifecycleTests` | The money rules at their boundaries (9,999.99 / 10,000 / 10,000.01), re-approval on edits, completion blocked, rejection fallbacks, "approve what you saw" |
| `Persistence/*` | What the **database** must guarantee even if app code is wrong: audit immutability, atomic audit, composite FKs, one pending revision, forced concurrency interleavings, and concurrent migrations on a brand-new database |
| `Authorization/*`, `Http/RequestAuthorizationHttpTests`, `AdminHttpTests` | Conflict of interest and privilege escalation, attempted the way an attacker would, over HTTP |
| `Http/SpendReportTests` | "Returns the right numbers": a **hand-computed fixture** with rows on the exact boundary instants, non-completed work, and another org's spend |
| `Http/SiteTests` | Any member can add a site, but it's **always bound to their org** (an injected org ID is ignored), invisible and unusable to other orgs, unique per org regardless of case (but not across orgs), and audited |
| `Http/AuthenticationTests`, `EncryptedLoginTests`, `ProductionModeTests` | Enumeration, revocation on the next request, replay, and production-only behaviour the Development suite would never see |

**Not tested, deliberately:** framework behaviour (binding, per-field validation attributes, EF mapping trivia), logging, and the client's markup (covered only by the browser smoke test).

```bash
dotnet test                       # needs Postgres; uses the database assessment_test (created automatically)
TEST_CONNECTION_STRING="Host=...;Database=...;Username=...;Password=..." dotnet test   # another server
```

**Browser smoke test (optional).** `tests/e2e/browser-smoke.js` drives the real UI in a headless browser against a **running** app, using an installed Edge (or Chrome with `BROWSER_CHANNEL=chrome`). It isn't part of `dotnet test`.
- It covers login, an HTML-injection description, approve/reject, completion, the audit trail, the spend report, the admin panel, a cross-tenant 404, logout, no password in any request body, and no JS or CSP errors.
- It signs in about 8 times against a rate limit of 10 per minute per IP, so allow a minute between runs.
- It uses your dev database: it restores the threshold it changes, but leaves a test user and some audit rows behind.

```bash
dotnet run --project src/Assessment.Api --launch-profile http   # terminal 1
cd tests/e2e && npm install && node browser-smoke.js            # terminal 2
```

## Configuration

**Using your own Postgres instead of Docker.** The default connection string (`appsettings.Development.json`) is `Host=localhost;Port=5432;Database=assessment;Username=postgres;Password=postgres`. If yours differs, override it without editing files:

```bash
dotnet user-secrets --project src/Assessment.Api set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=assessment;Username=me;Password=secret"
# or, for the current shell only:
export ConnectionStrings__Default="Host=...;Username=me;Password=secret"     # bash
$env:ConnectionStrings__Default="Host=...;Username=me;Password=secret"       # PowerShell
```

Is port 5432 taken by a local Postgres, but you still want the Docker one? Run `POSTGRES_PORT=5433 docker compose up -d db` and use `Port=5433`.

**Running outside Development.** Set `ASPNETCORE_ENVIRONMENT=Production` (or any non-Development value), plus:
- `ConnectionStrings__Default` and `LoginEncryption__PrivateKeyPem` (an RSA key of ≥ 3072 bits in PEM format), from a secret manager.
- Migrations don't run on startup there. Apply them with `dotnet ef database update --project src/Assessment.Api` (or a migration bundle) as a deploy step.
- HTTP is redirected to HTTPS and HSTS is sent. Behind a TLS-terminating proxy, configure forwarded headers.

**Database migrations.** `dotnet-ef` is pinned as a local tool:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Assessment.Api
```

## Project layout

```
src/Assessment.Api/
  Domain/                  entities, MaintenanceRequest aggregate, ApprovalRules, RequestTransitions, permissions
  Authorization/           session middleware, CurrentUser, permission policies, CanDecide / CanModify
  Features/                endpoints by feature: Auth, Requests, Sites, Admin, Reports
  Infrastructure/Data/     AppDbContext, entity configurations, interceptors, migrations, dev seeder
  Infrastructure/Tenancy/  TenantContext, tenant write guard
  wwwroot/                 static client (HTML + vanilla JS + CSS)
tests/Assessment.Api.Tests/  Domain · Persistence · Authorization · Http
tests/e2e/                 headless-browser smoke test
scripts/login.mjs          command-line login for curl users
docs/                      full decisions and AI-log records
docker-compose.yml         Postgres 16 for reviewers without a local install
```

## Troubleshooting

- **`fail: ... An error occurred using the connection to database 'assessment'` on first start.** Expected: EF Core checks whether the database exists before creating it. If `/health` returns `Healthy`, nothing is wrong.
- **`password authentication failed for user "postgres"`.** Your local Postgres uses different credentials; override the connection string as shown above.
- **`Connection string 'ConnectionStrings:Default' is missing`.** You're running outside Development; use `--launch-profile http` or set the connection string.
- **Login says "Password encryption needs a secure page".** Browsers only allow Web Crypto on HTTPS or `localhost`. Open `http://localhost:5183`, not a LAN IP.
- **Login says "The login challenge is invalid or has expired".** The app restarted (a new in-memory key) or the page sat for over 2 minutes. Reload and sign in again.
- **`429 Too Many Requests` on login.** That's the per-IP login rate limit (10 per minute). Wait a minute.
- **A fresh database has no demo requests.** Seeding runs once per database. To start clean, drop the `assessment` database and restart the app.
- **`dotnet test` fails to build with "file is locked by Assessment.Api".** Stop the running app first; on Windows it locks the build output.
