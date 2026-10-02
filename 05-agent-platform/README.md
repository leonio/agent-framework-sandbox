# 05 · Roster

An agent platform: reusable, versioned agents in a library; scenarios that compose them; runner pools that execute
the work; a per-assignment model picker that uses each person's own keys or Copilot seat; and a conversational retro
that feeds back into every agent.

Start with the design: [`docs/architecture.md`](docs/architecture.md).

## Status

| Slice | Scenario | State |
| --- | --- | --- |
| 1 | PR review: three reviewer agents, triage with reasons, retro | In progress: abstractions, agent library and catalog built; Keycloak sign-in in the API and the AppHost, run under Aspire; agent runtime done and smoke-run on the fake endpoint; platform, runner, rest of the API and web still to do |
| 2 | Incident triage, sandbox pool, Copilot-backed developer, lessons | Planned |
| 3 | Idea to code | Planned |

Picking this up? Read [`docs/HANDOFF.md`](docs/HANDOFF.md) first: it has the status, the decisions made since the design,
environment notes and an ordered plan. See the "Verified facts" and "Risks" sections of the design doc for what has and
hasn't been exercised.
