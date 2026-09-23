# AI Log

One page. Tool: Claude Code (Claude Opus 5.5); configuration in [`CLAUDE.md`](CLAUDE.md). The full chronological record is in [docs/ai-log-full.md](docs/ai-log-full.md).

## What I delegated, and how

| Level | Work |
|---|---|
| **Fully** | Scaffolding, `docker-compose.yml`, the static client's markup, first drafts of the docs. |
| **With tight constraints** | Domain, schema, tenancy, auth, endpoints, report. Constraints: build against the reviewed [PLAN.md](PLAN.md), one step at a time; stop for my review of the transition table before writing tests; read the generated migration SQL before committing; **every test suite must be seen failing against planted bugs**; commit per step. |
| **By hand (my decisions)** | PostgreSQL; three plan-review rounds that rewrote the approval, permission and audit rules; approving the transition table; the threshold-snapshot rule; accepting the OrgAdmin risk as transparency; requiring encrypted password fields over the agent's objection; testing the UI myself. |

## By area: what the agent did, what I decided, how it was checked

| Area | Agent | Me | Verified by |
|---|---|---|---|
| **Architecture** | Proposed one project organised by feature (`Domain` / `Authorization` / `Tenancy`), thin endpoints, one mutation path (load → authorize → domain → save) | Approved the single-project structure over the agent-considered Clean Architecture split. Moved self-approval out of the entity into the permission layer. Asked for the login → org-ID flow to be spelled out | ARCHITECTURE.md's enforcement map; a planted bug returning 403 instead of 404 for a foreign ID was caught |
| **Tenant isolation** | Designed 4 layers: org from the user's DB record → global query filters → a SaveChanges guard → composite FKs | Required every read to be org-locked, and the org to come only from the logged-in user's record | Cross-tenant tests with **real foreign IDs** on every endpoint. Convention tests (every entity filtered; `IgnoreQueryFilters` only in login). Planted bugs: filters off → 6 tests fail, guard off → 2. EF's SQL log shows the filter inside the report's subqueries |
| **Library & framework selection** | Proposed each dependency and argued against extras: no MediatR/AutoMapper (now commercial), `Stateless`, FluentValidation, or full Identity (only its `PasswordHasher`). Added one package, `EFCore.NamingConventions`, and `playwright-core` for the browser test only | Chose PostgreSQL. Approved .NET 10 LTS and cookie sessions over JWT | The agent dropped its own first fix (`[SkipValidation]`) when the build flagged it as evaluation-only (ASP0029). Every dependency is justified in DECISIONS.md |
| **Schema quality** | Composite `(id, organization_id)` FKs; CHECKs for costs, statuses and "completed ⇒ actual cost"; no cascades; text enums; immutable `request_revisions` | Asked for `created_at`/`updated_at` and user stamps on every table. Approved three changes at the step-1 review: pending/approved revision derived instead of stored; a `sequence` column; permissions as `text[]` | Read the generated migration SQL before committing (found a missing FK on `previous_revision_id`). Raw-SQL tests prove the DB rejects cross-org references, a second pending revision and self-decided revisions |
| **Indexing judgment** | Tied every index to a known query, e.g. a partial covering index for the report: `(org, site, completed_at) INCLUDE (actual_cost) WHERE status='Completed'` | No change requested. Keeping the "redundant" status filter was the agent's call, made on evidence | `EXPLAIN` on the SQL EF actually generates: an Index Only Scan with the filter, table reads without it. Accepted EF's automatic FK indexes as a simplicity trade-off (SCHEMA.md) |
| **Migrations** | Generated `InitialSchema`, and added the audit trigger as raw SQL in `Up`/`Down`; migrate-on-startup in Development only | Approved schema changes before the migration was generated | Read the generated SQL line by line; `has-pending-model-changes` after later model edits. It caught that the snake_case rename also renames EF's own history table, breaking older databases |

## A real task specification (verbatim, from my plan review)

> every maintance cost has a expected and actual cost, and every organization has threshold we can set it default 10k but organization can change it. if maitance request cost is under the threshold its autoapproved. if its over threshold it requires approval from everyone who has permission to approve apart from reqeust generator. every addition in maintance cost needs new approval if total is going above threshold.

The agent asked one clarifying question rather than guessing: whether "everyone" meant *any one* eligible approver or *all* of them. I answered *any one*.

## Plausible but wrong

**What:** the first plan argued that an Approver shouldn't change the threshold, because they *"could **lower** it, then raise their own request under it."* I accepted it and repeated it back.
**Why it's wrong:** lowering the threshold makes *more* requests need approval. The bypass is **raising** it. The conclusion was right and the reasoning backwards, so it would have gone wrong in DECISIONS.md and in the interview.
**How it was caught:** my next answer said admins can "**increase**" it; the agent noticed the contradiction and corrected the plan.
**Why it was easy to miss:** the sentence has the right shape (actor, action, exploit), and since the conclusion was right, nothing downstream broke.

**Also:** a test suite that *looked* complete. The "can't approve your own request" tests all passed, but deleting that check entirely **left all 93 tests green**. Every test's owner was also the author of the pending change, so a different rule always refused them first. It was found by planting the bug, and fixed with a "colleague edits your request" test.

## Tests: state transitions enforced

- **The spec came first.** The agent wrote `RequestTransitions` as an explicit table, `(status, pending kind) → allowed actions`, and **stopped for my review** before any test was written. I approved it.
- **Exhaustive, not happy-path.** `TransitionTableTests` drives a real request into each of the 6 reachable states and tries **all 4 actions in every state**: 24 cases, plus one proving the transient `Raised` state is never observable. The expected table is **restated independently in the test**, so the test isn't a copy of the code.
- **It fails when it should.** Planting an extra legal action (Approve from Approved) failed exactly that case.
- **The same rules hold over HTTP.** An illegal transition or a stale `revisionId` returns **409** (`Stale_revision_and_illegal_transition_are_conflicts`), and two approvers deciding at once gives one 200 and one 409.

## How I checked the output

- **Planted bugs for every suite** (26 mutations). One survived and exposed the gap above; one survived *as predicted* (an equivalent mutation), kept for index use and proven with `EXPLAIN`.
- **Ran the real app before writing HTTP tests:** a curl walkthrough found three bugs that 94 green tests missed (a 500 from .NET 10 validation, a false 409 from EF's Guid-key convention, and a 500 on malformed input).
- **Headless-browser smoke test** (installed Edge): HTML injection renders as text, cross-tenant 404, and no password in any request payload. It found a login race that could put credentials in the URL, and a visible `null` in the header.
- **Captured EF's real SQL** to confirm the tenant filter sits inside the report's subqueries.
- **Rejected agent shortcuts:** `[SkipValidation]` (evaluation-only API) and suppressing its warning; loosening the login rate limit to make a test pass.
