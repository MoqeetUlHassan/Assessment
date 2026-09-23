# AI Log

Tool: Claude Code (Claude Opus 5.5). Agent configuration: [`CLAUDE.md`](CLAUDE.md).

## What was delegated, and how

| Level | Work |
|---|---|
| **Delegated fully** | Project scaffolding (`dotnet new`, solution wiring, `.gitignore`, `global.json`), `docker-compose.yml`, first drafts of README / DECISIONS. |
| **Delegated with tight constraints** | _To fill in during feature work._ |
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

## Plausible but wrong

**What:** In the v1 plan, the agent argued against letting Approvers change the threshold: *"An Approver who could change it could **lower** it, then raise their own request under it: a self-approval bypass."* I accepted this and repeated it back in my review answers.

**Why it's wrong:** it's backwards. Lowering the threshold makes *more* requests need approval, which is the safe direction. The bypass is **raising** it, so that your own large request falls under the auto-approve line. The conclusion (restrict who can change it) was right, but the reasoning would have been wrong in DECISIONS.md and wrong in the interview.

**How it was caught:** when I answered that "only organization admins can **increase** it", the direction in my answer contradicted the direction in the plan's rationale, and the agent flagged it before rewriting the plan.

**Why it was easy to miss:** the sentence has the right shape: actor, action, exploit. The conclusion was correct, so nothing downstream looked broken. You only notice by working through which direction reduces how many requests need approval.

Also noted: the first startup logs `fail: ... An error occurred using the connection to database 'assessment'` even though startup succeeded (it's EF checking whether the DB exists). An agent reading logs could "fix" this non-problem, or learn to ignore real connection errors.

## Checks I did on agent output

- Ran the app and hit `/health` against the real local Postgres rather than trusting the build.
- Pointed the health test at a dead port to confirm it **fails** when the DB is unreachable, so it isn't passing vacuously.
