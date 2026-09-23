# AI Log

One page. Tool: Claude Code (Claude Opus 5.5); configuration in [`CLAUDE.md`](CLAUDE.md). The full chronological record is in [docs/ai-log-full.md](docs/ai-log-full.md).

## What I delegated, and how

| Level | Work |
|---|---|
| **Fully** | Scaffolding, `docker-compose.yml`, the static client's markup, first drafts of the docs. |
| **With tight constraints** | Domain, schema, tenancy, auth, endpoints, report. Constraints: build against the reviewed [PLAN.md](PLAN.md), one step at a time; stop for my review of the transition table before writing tests; read the generated migration SQL before committing; **every test suite must be seen failing against planted bugs**; commit per step. |
| **By hand (my decisions)** | PostgreSQL; three plan-review rounds that rewrote the approval, permission and audit rules; approving the transition table; the threshold-snapshot rule; accepting the OrgAdmin risk as transparency; requiring encrypted password fields over the agent's objection; testing the UI myself. |

## A real task specification (verbatim, from my plan review)

> every maintance cost has a expected and actual cost, and every organization has threshold we can set it default 10k but organization can change it. if maitance request cost is under the threshold its autoapproved. if its over threshold it requires approval from everyone who has permission to approve apart from reqeust generator. every addition in maintance cost needs new approval if total is going above threshold.

The agent asked one clarifying question rather than guessing: whether "everyone" meant *any one* eligible approver or *all* of them. I answered *any one*.

## Plausible but wrong

**What:** the first plan argued that an Approver shouldn't change the threshold, because they *"could **lower** it, then raise their own request under it."* I accepted it and repeated it back.
**Why it's wrong:** lowering the threshold makes *more* requests need approval. The bypass is **raising** it. The conclusion was right and the reasoning backwards, so it would have gone wrong in DECISIONS.md and in the interview.
**How it was caught:** my next answer said admins can "**increase**" it; the agent noticed the contradiction and corrected the plan.
**Why it was easy to miss:** the sentence has the right shape (actor, action, exploit), and since the conclusion was right, nothing downstream broke.

**Also:** a test suite that *looked* complete. The "can't approve your own request" tests all passed, but deleting that check entirely **left all 93 tests green**. Every test's owner was also the author of the pending change, so a different rule always refused them first. It was found by planting the bug, and fixed with a "colleague edits your request" test.

## How I checked the output

- **Planted bugs for every suite** (26 mutations). One survived and exposed the gap above; one survived *as predicted* (an equivalent mutation), kept for index use and proven with `EXPLAIN`.
- **Ran the real app before writing HTTP tests:** a curl walkthrough found three bugs that 94 green tests missed (a 500 from .NET 10 validation, a false 409 from EF's Guid-key convention, and a 500 on malformed input).
- **Headless-browser smoke test** (installed Edge): HTML injection renders as text, cross-tenant 404, and no password in any request payload. It found a login race that could put credentials in the URL, and a visible `null` in the header.
- **Captured EF's real SQL** to confirm the tenant filter sits inside the report's subqueries.
- **Rejected agent shortcuts:** `[SkipValidation]` (evaluation-only API) and suppressing its warning; loosening the login rate limit to make a test pass.
