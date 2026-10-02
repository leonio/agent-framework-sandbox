# 05 · Roster

An agent platform: reusable, versioned agents in a library; scenarios that compose them; runner pools that execute
the work; a per-assignment model picker that uses each person's own keys or Copilot seat; and a conversational retro
that feeds back into every agent.

Start with the design: [`docs/architecture.md`](docs/architecture.md).

## Status

| Slice | Scenario | State |
| --- | --- | --- |
| 1 | PR review: three reviewer agents, triage with reasons, retro | Done and run end to end under Aspire through the web app, on the offline fake endpoint. Not yet run against a real model or a real GitHub pull request (both compiled only) |
| 2 | Incident triage, sandbox pool, Copilot-backed developer, lessons | Planned |
| 3 | Idea to code | Planned |

## Run it

Needs the .NET 10 SDK, Node 22 and Docker.

```bash
cd src/Roster.AppHost
dotnet run
```

The Aspire dashboard link is printed on start. Open the web app at <http://localhost:5173> and sign in as
`admin@roster.local`; the password is generated on first run and kept in user secrets
(`dotnet user-secrets list`, `Parameters:roster-admin-password`). Or create an account from the sign-in page. The
shared `Fake (offline)` endpoint needs no key: start an assignment on the sample pull request to see the reviewers,
triage and the retro.

Picking this up? Read [`docs/HANDOFF.md`](docs/HANDOFF.md) first: it has the status, the decisions made since the design,
environment notes and an ordered plan. See the "Verified facts" and "Risks" sections of the design doc for what has and
hasn't been exercised.
