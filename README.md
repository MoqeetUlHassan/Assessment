# Assessment

ASP.NET Core (.NET 10) Web API on PostgreSQL.

Backend for facilities maintenance requests: multi-tenant organisations, sites, permission-based roles, threshold-based approval with re-approval on changes, a spend report, and an immutable audit trail.

| Doc | What's in it |
|---|---|
| [PLAN.md](PLAN.md) | Agreed design: data model, permissions, approval rules, transition table, API, tests |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Layers, request flow, where each guarantee is enforced (diagrams) |
| [SCHEMA.md](SCHEMA.md) | ER diagram, composite FKs, constraints, indexes and the query each serves |
| [DECISIONS.md](DECISIONS.md) | Technical choices, what was rejected, trade-offs, assumptions |
| [AI-LOG.md](AI-LOG.md) | How the AI agent was used and steered |
| [CLAUDE.md](CLAUDE.md) | Agent configuration and rules |

> Implementation in progress. Run instructions below cover the current state.

## Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 10.0.x (pinned in `global.json`, later 10.0 feature bands accepted) | `dotnet --list-sdks` |
| PostgreSQL | 14+ — **either** Docker **or** a local install | `docker --version` / `psql --version` |
| Node.js *(optional)* | 18+, only for command-line login (`scripts/login.mjs`) and the browser smoke test | `node --version` |

## Run it (about 5 minutes on a clean machine)

```bash
git clone <repo-url> && cd Assessment

# 1. Start Postgres (skip if you already have one on localhost:5432 with user/password postgres/postgres)
docker compose up -d db

# 2. Run the API. It creates the database and applies migrations on startup.
dotnet run --project src/Assessment.Api --launch-profile http
```

Then open **http://localhost:5183** and sign in with a seeded account (below). The minimal UI covers login, the request list (filter and paging), raising a request, and a detail page with approve/reject, edit, record the actual cost, revisions and the audit trail. Sign in as `admin@acme.test` to reach the **Admin** page: threshold, users, roles and permissions, and the organization audit log. Approvers and admins also get **Spend report**: the total actual cost of completed requests per site over a date range (the current month by default).

Check it's up:

```bash
curl http://localhost:5183/health     # -> Healthy
```

OpenAPI document (Development only): http://localhost:5183/openapi/v1.json

### Seeded accounts (Development only)

On first start in Development, two organizations are created so you can try tenant isolation by hand. **Every account's password is `ChangeMe-Dev-2026!`**

| Organization | Email | Role | Can |
|---|---|---|---|
| Acme Retail | `admin@acme.test` | OrgAdmin | everything; manage users, roles, threshold (never approve own requests) |
| Acme Retail | `approver1@acme.test`, `approver2@acme.test` | Approver | raise, edit any, approve/reject, spend report |
| Acme Retail | `requester@acme.test` | Requester | raise, edit/complete own |
| Globex Offices | `admin@globex.test`, `approver1@globex.test`, `approver2@globex.test`, `requester@globex.test` | same roles | same, isolated from Acme |

Sites: Acme has *Site 12, Downtown Store, Warehouse North*; Globex has *HQ Tower, Site 12, Data Centre*. A fresh database also gets a few demo requests in different states (completed, approved, pending approval), created through the real domain methods with genuine audit trails, so the list and this month's spend report aren't empty. Both have a "Site 12", which is deliberate: names don't cross tenants. Seeding is off outside Development (`Seed:DevelopmentData`).

### Try the API with curl

The login endpoint accepts only an **encrypted password field**, never a plain one (see DECISIONS.md). A plain curl call therefore can't log in; `scripts/login.mjs` (Node 18+) does what the browser does and saves the session cookie to a curl cookie jar:

```bash
node scripts/login.mjs approver1@acme.test 'ChangeMe-Dev-2026!' jar.txt   # challenge → encrypt → login → jar.txt

curl -b jar.txt http://localhost:5183/api/me          # who am I, which org, which permissions
curl -b jar.txt -X POST http://localhost:5183/api/auth/logout
```

A full request lifecycle (requester raises 15,000 against a 10,000 threshold → an approver approves → actual cost → approved → Completed):

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
| GET | `/api/auth/login-challenge` | — | `{ keyId, publicKey, nonce }` for encrypting the password (rate-limited) |
| POST | `/api/auth/login` · `/api/auth/logout` | — | login body `{ email, keyId, encryptedPassword }`; rate-limited per IP |
| GET | `/api/me` | session | user, role, permissions, org, threshold |
| GET | `/api/sites` | session | your org's sites |
| GET | `/api/requests?status=&siteId=&page=&pageSize=` | session | whole org, newest first, paged (≤ 100) |
| POST | `/api/requests` | `requests.create` | `{ siteId, description, estimatedCost }` |
| GET | `/api/requests/{id}` | session | includes revisions and `actions` the caller may take |
| PUT | `/api/requests/{id}` | own request, or `requests.manage` | `{ description, estimatedCost, reason }` |
| POST | `/api/requests/{id}/complete` | own request, or `requests.manage` | `{ actualCost, reason? }` |
| POST | `/api/requests/{id}/approve` | `requests.approve`, not your request/revision | `{ revisionId, comment? }` |
| POST | `/api/requests/{id}/reject` | `requests.approve`, not your request/revision | `{ revisionId, reason }` |
| GET | `/api/requests/{id}/history` | session | revisions + audit events with actor names |
| GET | `/api/reports/spend?from=yyyy-MM-dd&to=yyyy-MM-dd` | `reports.spend` | spend per site, caller's org only; inclusive UTC dates, ≤ 366 days |
| GET / POST | `/api/admin/users` | `admin.users` | list / create `{ displayName, email, password, roleId }` |
| PUT | `/api/admin/users/{id}/role` | `admin.users` | `{ roleId }`; not yourself |
| POST | `/api/admin/users/{id}/deactivate` · `/reactivate` | `admin.users` | not yourself; ends their sessions |
| POST | `/api/admin/users/{id}/password` | `admin.users` | `{ password }` (≥ 12); ends their sessions |
| GET / POST | `/api/admin/roles` | `admin.roles` | list / create `{ name, permissions[] }` |
| PUT | `/api/admin/roles/{id}/permissions` | `admin.roles` | `admin.*` can't be granted; OrgAdmin role locked |
| GET | `/api/admin/permissions` | `admin.roles` | the permission catalog |
| PUT | `/api/admin/settings/threshold` | `admin.settings` | `{ amount, reason }`; applies to new requests only |
| GET | `/api/admin/audit?entityType=&page=` | `admin.users` | organization-wide audit log |

Errors are `application/problem+json`:

| Status | When |
|---|---|
| 400 | Invalid or malformed input |
| 401 | No session |
| 403 | Missing permission, or your own request / revision |
| 404 | Doesn't exist, **or belongs to another organization** (indistinguishable) |
| 409 | Illegal transition, a stale `revisionId`, or a concurrent change |

### Using your own Postgres instead of Docker

The default connection string (in `src/Assessment.Api/appsettings.Development.json`) is:

```
Host=localhost;Port=5432;Database=assessment;Username=postgres;Password=postgres
```

If your credentials differ, override the connection string without editing files. Either:

```bash
# user-secrets (persisted per machine, outside the repo)
dotnet user-secrets --project src/Assessment.Api init
dotnet user-secrets --project src/Assessment.Api set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=assessment;Username=me;Password=secret"

# or an environment variable, for the current shell only
export ConnectionStrings__Default="Host=...;Username=me;Password=secret"     # bash
$env:ConnectionStrings__Default="Host=...;Username=me;Password=secret"       # PowerShell
```

Port 5432 already taken by a local Postgres but you still want the Docker one? Run `POSTGRES_PORT=5433 docker compose up -d db`, then override the connection string with `Port=5433`.

## Run the tests

The tests boot the real app against a **real Postgres** database named `assessment_test`, which is created automatically. Postgres must be running (step 1 above).

```bash
dotnet test
```

To point the tests at a different server, set `TEST_CONNECTION_STRING`.

### Browser smoke test (optional)

`tests/e2e/browser-smoke.js` drives the UI in a real headless browser against a **running** app. It covers login, raising a request with an HTML-injection description, approve/reject, completion, the audit trail, a cross-tenant 404, logout, and no JS or CSP errors. It uses an installed Edge (or Chrome with `BROWSER_CHANNEL=chrome`) and isn't part of `dotnet test`.

```bash
dotnet run --project src/Assessment.Api --launch-profile http   # in one terminal
cd tests/e2e && npm install && node browser-smoke.js            # in another
```

It signs in about 8 times, and the login rate limit is 10 per minute per IP, so allow a minute between runs. It runs against your dev database: it restores the threshold it changes, but it leaves one test user and some audit rows behind.

## Database migrations

`dotnet-ef` is pinned as a local tool:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Assessment.Api
```

In Development, migrations are applied automatically on startup. In any other environment they are not (see DECISIONS.md).

## Project layout

```
src/Assessment.Api/          Web API (minimal APIs, EF Core, Npgsql)
  Data/AppDbContext.cs       EF Core context; entity configs picked up by assembly scan
tests/Assessment.Api.Tests/  xUnit integration tests via WebApplicationFactory
docker-compose.yml           Postgres 16 for local dev / reviewers
```

## Troubleshooting

- **`fail: ... An error occurred using the connection to database 'assessment'` on first start.** This is expected: EF Core checks whether the database exists before creating it. If `/health` returns `Healthy`, nothing is wrong.
- **`Connection string 'ConnectionStrings:Default' is missing`**: you are running outside the Development environment. Use `--launch-profile http`, or set the connection string as shown above.
- **`password authentication failed for user "postgres"`**: your local Postgres uses different credentials. Override them as shown above.
