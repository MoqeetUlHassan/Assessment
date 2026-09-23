# AI Log (full record)

> The one-page summary is [../AI-LOG.md](../AI-LOG.md). This is the complete, chronological record of steering, findings and checks.

Tool: Claude Code (Claude Opus 5.5). Agent configuration: [`CLAUDE.md`](../CLAUDE.md).

## What was delegated, and how

| Level | Work |
|---|---|
| **Delegated fully** | Project scaffolding (`dotnet new`, solution wiring, `.gitignore`, `global.json`), `docker-compose.yml`, first drafts of README / DECISIONS. |
| **Delegated with tight constraints** | Domain model, schema and tenancy (steps 1–3). Constraints: implement against PLAN.md; stop for my hand review of the transition table before tests; every new test must be seen failing (planted bugs); read the generated migration SQL before committing. |
| **Done by hand** | Choosing PostgreSQL. Deciding to use my existing local Postgres rather than Docker. Reviewing the plan and rewriting the approval / permission / audit rules (v1 → v2, below). _More during feature work._ |

## Task specifications / prompts (verbatim)

**Setup prompt (session 1):**

> create new directory with .net project and git init. i suggest using postgreSQL if u have any other recomendation tell me reason
> 1. README.md — how to run it. […] 2. DECISIONS.md […] 3. AI-LOG.md […] 4. Your agent configuration […] 5. Commit history […] 6. A few tests […]
> once this structure is done i will start with project requirements

The agent found that no .NET SDK was installed (only runtimes) and that Docker was missing. It asked before installing the SDK and before choosing how to run Postgres. It recommended Docker Compose; I chose my local Postgres, and it kept `docker-compose.yml` for reviewers.

## Steering the plan (v1 → v2)

The draft plan (commit `5da9b7e`) was reviewed and changed before any code was written. Main corrections I made:

- **Moved the self-approval rule** out of the entity and into the permission layer (a resource-based authorization handler). "Who may act" is authorization, not state logic.
- **Replaced one-shot approval with cost revisions.** Every money change is re-checked against the threshold; any increase that lands at or above it needs a fresh approval. The agent's "flag and acknowledge" overrun design became "completion blocked until approved".
- **Changed three defaults:** Requesters see the whole org, not just their own requests; an OrgAdmin role manages the threshold through an API; and there are `created_at`/`updated_at` columns on every table.
- **Asked for the login → org ID flow to be spelled out.** The draft said "org ID comes from the cookie", which read as if a cookie existed before login.

**v2 → v3 → v3.1** (commits `560eb39`, `f3f86ad`):
- **Roles now carry permissions**, and checks use permissions. OrgAdmin manages users and roles.
- **Edits while pending** supersede the pending revision and need re-approval.
- **The actual cost** needs approval whenever it's at or above the threshold.
- **The agent flagged that an all-powerful OrgAdmin could self-approve**, via a threshold raise or a sock-puppet account. I decided:
  - no one approves their own request, OrgAdmin included;
  - a threshold change applies only to requests created *after* it (snapshot per request);
  - everything is user-stamped, so the remaining risk is visible in the audit.

  I accepted transparency over prevention; the agent recorded four-eyes as the production option.

## Plausible but wrong

**What:** In the v1 plan, the agent argued against letting Approvers change the threshold: *"An Approver who could change it could **lower** it, then raise their own request under it: a self-approval bypass."* I accepted this and repeated it back in my review answers.

**Why it's wrong:** it's backwards. Lowering the threshold makes *more* requests need approval, which is the safe direction. The bypass is **raising** it, so that your own large request falls under the auto-approve line. The conclusion (restrict who can change it) was right, but the reasoning would have been wrong in DECISIONS.md and wrong in the interview.

**How it was caught:** when I answered that "only organization admins can **increase** it", the direction in my answer contradicted the direction in the plan's rationale, and the agent flagged it before rewriting the plan.

**Why it was easy to miss:** the sentence has the right shape: actor, action, exploit. The conclusion was correct, so nothing downstream looked broken. You only notice by working through which direction reduces how many requests need approval.

**Second instance (step 2, caught by the agent):** switching to snake_case naming (`UseSnakeCaseNamingConvention`) also renames EF's own `__EFMigrationsHistory` columns (`MigrationId` → `migration_id`). The dev and test databases had been created by earlier runs with the old column names, so `dotnet ef migrations remove` failed with `42703: column "migration_id" does not exist`. **Why it was easy to miss:** a clean machine never hits it, and the build, the migration generation and the generated SQL all looked correct. It only surfaced because an existing database was touched. Both databases held only an empty history table, so they were dropped and recreated.

**Third instance (step 4): a test suite that looked complete but wasn't.** The authorization tests covered "an Approver can't approve their own request" and "can't approve a revision they submitted", and all passed. Planting a bug that **deleted the own-request check entirely** left all 93 tests green. In every test where the approver owned the request, they had also authored the pending revision, so the own-revision rule always denied them first and the own-request rule was never exercised on its own. The missing case: *a colleague edits your request, and you try to approve their edit.* A test was added (`Approver_cannot_approve_a_colleagues_edit_of_their_own_request`), and the planted bug now fails it. **Why it was easy to miss:** the test names read like full coverage of both rules, and reading the tests wouldn't reveal the overlap. Only removing the code did.

**Also in step 4 (found by the tests):** parallel test hosts each ran the dev seeder on startup and deadlocked (`40P01`) inserting the same rows. The fix is a Postgres advisory lock in the seeder, not a test workaround; two dev instances starting together would have hit the same race.

**Fourth instance (step 5): three bugs that 94 green tests missed, found by running the app.** Before writing the HTTP tests, I drove the real app with a curl script: create → self-approve → cross-org → stale revision → approve → complete → approve → history. It found:
1. **Every approve/edit/complete call returned 500.** .NET 10's built-in validation walked the `[AsParameters]` service bundle into the DbContext's object graph and hit its depth limit. The agent's first fix was `[SkipValidation]`, but the build then flagged it as evaluation-only (ASP0029). Rejected; the services became plain parameters.
2. **Completing an approved request returned 409 "changed by someone else"** on a plain sequential call. EF treats Guid keys as store-generated by default, so a *new* revision (domain-assigned ID) added to a loaded request was sent as an UPDATE matching 0 rows. No test added a child to a loaded aggregate: creation adds everything at once, and the persistence test only approved.
3. **A malformed `revisionId` returned 500 instead of 400.**

**Why they were easy to miss:** each layer was tested in isolation and passed. The domain tests never touch EF, and the persistence tests never went through HTTP binding or validation. All three now have regression tests, and each was confirmed by a planted bug.

**Fifth instance (client): a login race only a real browser exposed.** `login.js` first awaited a "am I already signed in?" call, and only *then* attached the form's submit handler. `node --check` passed and the code read fine. The headless-browser smoke test clicked "Sign in" before the handler existed, so the form fell through to a native submission. Without `method` that's a **GET with the email and password in the URL**, which ends up in browser history and server logs. Fixed by attaching the handler synchronously first and adding `method="post"`. The smoke test now also asserts the password never appears in the URL.

**Also found by the browser test (step 6):**
- **A visible `null` in the header for every non-admin.** `renderHeader` passed `null` (in place of the Admin link) straight to `replaceChildren()`, which prints it as text. The safe `el()` helper filters nulls, but this call bypassed it. The smoke test's header check only looked for the org and user name, so it was widened.
- **An apparently flaky run that wasn't:** back-to-back smoke runs got `429`, because the login rate limit (10/min per IP) was doing its job. I kept the security setting as it is; the test now reports the rate limit explicitly, and the README says to allow a minute between runs.

**A decision where I overrode the agent: encrypting the password field.** Seeing the password in the DevTools request payload, I asked for it to be encrypted in the payload. The agent argued the request is already encrypted by HTTPS, that DevTools shows the pre-encryption view, and that app-level encryption doesn't stop an attacker in the middle without TLS. It recommended enforcing HTTPS everywhere. I kept the requirement: *a captured payload must not reveal the password.* The agent then implemented it so it achieves that: public-key encryption with a **single-use nonce inside the ciphertext**, so captured payloads can't be replayed either. The plain field is rejected, and a Node script covers command-line logins. It recorded the limits in DECISIONS.md, and it flagged that the admin "create user" and "reset password" forms still sent new passwords as plain fields. On my go-ahead, those were moved to the same encryption, along with a renamed, generic `password-challenge`.

**Found by me while using the UI:** the edit "Reason for change" looked like it wasn't saved, and the "Comment" column was always empty. The database showed both were saved correctly (on the revision and in the audit details). The Revisions table simply had no Reason column, and "Comment" only ever showed the approver's decision note. It's fixed with a **Reason** column and a **Decision note** label, and the browser smoke test now edits with a reason and approves with a comment, checking both are visible. Lesson: the automated checks asserted status and audit counts, not that user-entered text is shown back.

**Found by me while using the UI (2):** as the Requester Riley, an Approver's request said *"You can't decide this one: you raised it, submitted this change, or lack the approve permission"*, which reads as if Riley raised it. The permissions were right (a Requester has no `requests.approve`, and can only edit their own requests); the **message** was a catch-all. The authorization rules now return *why* they refuse (`WhyCannotDecide` / `WhyCannotModify`; `CanDecide` is just "no reason"), so the page, and any 403, can't disagree with the check. A failed endpoint permission policy now also names the missing permission instead of a bare "Forbidden". The once-surviving planted bug (deleting the own-request rule) was re-run against the restructured code and is caught by 3 tests.

**Found by an independent review (step 9+):** I had a separate agent session review the app against the brief's PDF without changing anything. It cloned with a cold NuGet cache onto a spare port, and ran 63 API checks plus the browser smoke test. All requirements passed, but the first `dotnet test` on a **brand-new** test database failed one test every time (a different test each run). The cause: parallel test hosts all ran `MigrateAsync`, and two ran `RequestListIndexes`, so one failed dropping an index the other had already dropped. The seeder had an advisory lock; migration didn't. My earlier timed fresh-clone run predated that second migration, so it passed. Fixed with `DatabaseMigrator` (an advisory lock on the maintenance database, since the target doesn't exist yet on a fresh setup). A regression test runs 4 concurrent migrations on a throwaway database: 3/3 fail without the lock, 3/3 pass with it. The full suite then passed on three consecutive brand-new databases. The review also found `global.json` pinned to 10.0.401, which locks out older .NET 10 SDKs; it now accepts 10.0.100+.

Also noted: the first startup logs `fail: ... An error occurred using the connection to database 'assessment'` even though startup succeeded (it's EF checking whether the DB exists). An agent reading logs could "fix" this non-problem, or learn to ignore real connection errors.

## Checks I did on agent output

- Ran the app and hit `/health` against the real local Postgres rather than trusting the build.
- Pointed the health test at a dead port to confirm it **fails** when the DB is unreachable, so it isn't passing vacuously.
- **Planted bugs (mutation checks) for every test suite so far.** Each bug was caught by exactly the tests aimed at it:
  - domain: `>=` → `>`; an extra legal action;
  - schema: audit triggers dropped; the audit interceptor unregistered;
  - tenancy: query filters removed; the guard's owner check disabled; a stray `.IgnoreQueryFilters()` call;
  - auth: own-revision check removed; the session middleware trusting the cookie; the own-request check removed. **That last one survived at first; see "Third instance" above.**
  - requests: aggregate touch removed; store-generated IDs; 403 instead of 404 for a foreign ID; the malformed-input mapping removed.
  - admin: self-deactivation allowed; role lookup bypassing the org filter; password-reset audit removed; admin-only permissions made grantable.
  - report: inclusive end boundary; summing estimates; dropping the +1 day. All were caught.
  - encrypted login: nonce checked but not consumed (replay); key-ID check removed. Both were caught. The browser test inspects every real login request body and asserts that no password appears.
  - Removing the report's `status = 'Completed'` filter **survived, as predicted in advance.** It's an equivalent mutation, because `completed_at` is only set on completion. Rather than delete the "redundant" filter, I captured the SQL EF generates and ran `EXPLAIN`. With the filter, it's an Index Only Scan on the partial covering index; without it, an FK index plus table reads. The filter stays, for performance rather than correctness, and DECISIONS.md says so.
- **The report SQL was captured from EF's command log.** It shows the tenant filter (`organization_id = @ef_filter__CurrentOrganizationId`) applied inside both correlated subqueries without the report code mentioning organizations. That's direct evidence that isolation layer 2 covers queries written without thinking about tenancy.
- **A headless-browser smoke test** (installed Edge + `playwright-core`, kept in `tests/e2e/`) of the real UI. It checks an HTML-injection description renders as text and the injected script never runs, a cross-tenant 404, and no JS or CSP errors.
- **A timed fresh-clone run** (step 9): clone → build → first start on an empty database → CLI login took 17 s, and the suite passed 137/137 on another fresh database. The report showed exactly the seeded totals. Temporary databases and the clone were removed afterwards.
- **A manual end-to-end curl walkthrough before writing HTTP tests.** It found three bugs the tests had missed ("Fourth instance").
- **Small misses caught in step 3:**
  - A guard comment said "applies even to system scope" while the code returned early for system scope. The structure was fixed so the comment is true.
  - The first `IgnoreQueryFilters` scanner flagged a doc comment. It now matches call-shaped usage.
