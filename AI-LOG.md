# AI Log

Tool: Claude Code (Claude Opus 5.5). Agent configuration: [`CLAUDE.md`](CLAUDE.md).

## What was delegated, and how

| Level | Work |
|---|---|
| **Delegated fully** | Project scaffolding (`dotnet new`, solution wiring, `.gitignore`, `global.json`), `docker-compose.yml`, first drafts of README / DECISIONS. |
| **Delegated with tight constraints** | _To fill in during feature work._ |
| **Done by hand** | Choosing PostgreSQL. Deciding to use my existing local Postgres rather than Docker. _More during feature work._ |

## Task specifications / prompts (verbatim)

**Setup prompt (session 1):**

> create new directory with .net project and git init. i suggest using postgreSQL if u have any other recomendation tell me reason
> 1. README.md — how to run it. […] 2. DECISIONS.md […] 3. AI-LOG.md […] 4. Your agent configuration […] 5. Commit history […] 6. A few tests […]
> once this structure is done i will start with project requirements

The agent found that no .NET SDK was installed (only runtimes) and that Docker was missing. It asked before installing the SDK and before choosing how to run Postgres. It recommended Docker Compose; I chose my local Postgres, and it kept `docker-compose.yml` for reviewers.

## Plausible but wrong

_To fill in with a real instance from feature work. Don't invent one._

Candidate to watch for: the first startup logs `fail: ... An error occurred using the connection to database 'assessment'` even though startup succeeded (EF's existence probe). An agent reading logs could "fix" a non-problem, or learn to ignore real connection errors.

## Checks I did on agent output

- Ran the app and hit `/health` against the real local Postgres rather than trusting the build.
- Pointed the health test at a dead port to confirm it **fails** when the DB is unreachable, so it isn't passing vacuously.
