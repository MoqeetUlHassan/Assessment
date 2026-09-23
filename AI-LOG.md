# AI Log

Tool: Claude Code (Claude Opus 5.5). Agent configuration: [`CLAUDE.md`](CLAUDE.md).

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
- **A manual end-to-end curl walkthrough before writing HTTP tests.** It found three bugs the tests had missed ("Fourth instance").
- **Small misses caught in step 3:**
  - A guard comment said "applies even to system scope" while the code returned early for system scope. The structure was fixed so the comment is true.
  - The first `IgnoreQueryFilters` scanner flagged a doc comment. It now matches call-shaped usage.
