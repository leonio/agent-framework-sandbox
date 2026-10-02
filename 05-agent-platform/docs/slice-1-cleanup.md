# Roster slice 1: cleanup

Four small tasks that finish slice 1, written to be done on your own machine. They are track 0 of
[`slice-2.md`](slice-2.md); this file has the detail. Slice 1 itself is done and merged: everything here is either the
check that could not run in the cloud sandbox (0.3), or places where the docs and the code disagree.

| Task | What | Who | State |
|---|---|---|---|
| 0.3 | Real-model check, and a real GitHub pull request | you (needs your key and network) | todo |
| 0.1 | Make the design doc match the code | anyone | todo |
| 0.2 | Clear stored reasoning after a set time, as the design promises | anyone | todo |
| 0.4 | Edit endpoints in the UI; admins see everyone's assignments (optional) | anyone | todo |

Suggested order: **0.3 first** (it is the one only your machine can do, and it may turn up fixes), then 0.1, 0.2, 0.4.
Same working agreement as always: small commits, comments, no tests, verify by running.

## Running it on your machine

- Needs the .NET 10 SDK, Node 22 or later, and Docker Desktop running.
- Check out the branch (`claude/modest-lamport-rm5j2v`, or the default branch once this file is merged there).
- `cd 05-agent-platform/src/Roster.AppHost && dotnet run`. The first run fetches the Aspire CLI, generates the secrets
  (kept in user secrets) and prints the dashboard link. It also creates an ASP.NET Core dev certificate, which makes
  Keycloak serve HTTPS on `https://localhost:8080`; if the browser warns, run `dotnet dev-certs https --trust`.
- Images pull straight from `quay.io` and Docker Hub on a normal network. The `mirror.gcr.io` steps in the HANDOFF
  were for the cloud sandbox only.
- Open <http://localhost:5173> and sign in as `admin@roster.local`. The password is in
  `dotnet user-secrets list` (in `src/Roster.AppHost`), under `Parameters:roster-admin-password`.
- Using Claude Code locally? Point it at this file and [`HANDOFF.md`](HANDOFF.md).

## 0.3 Real-model check

Slice 1 has only ever met the offline fake endpoint. This is the check that it works with a real model, and with a
real pull request from GitHub.

1. **Add a key.** Settings → Keys → API key. Skip this for a local server that needs none (Ollama, LM Studio).
2. **Add an endpoint.** Settings → Model endpoints → Add an endpoint → OpenAI-compatible:
   - Base URL: blank for OpenAI itself; otherwise your server's OpenAI-compatible URL (`http://localhost:11434/v1` for
     Ollama; the runners run on your machine, so `localhost` works).
   - Default model: one your key can use. Tier models are optional.
   - Native structured output: on if the model supports JSON-schema output, off if not (the reviewers are then asked
     for JSON in the prompt, with one repair attempt).
   - Key: the one from step 1.
3. **Review the sample pull request** on that endpoint (New assignment → the sample pull request → pick the endpoint).
4. **Look at, and note down:**
   - Each reviewer's outcome (stepper and Ledger tab): `succeeded`, `repaired`, `invalid-output` or `failed`. A
     failed row's error says why. The likeliest failure is a provider rejecting the strict JSON schema; if so, try the
     same model with native structured output off.
   - Whether the findings are any good. The sample has a committed secret, SQL built by string interpolation, a card
     number written to the logs, an `HttpClient` created per call, and a description that tries to instruct AI
     reviewers (which should be reported, not obeyed).
   - Tokens and time on each ledger row. Reasoning appears only if the endpoint returns it (many do not).
   - The Aspire dashboard's trace for the assignment: the `chat` spans with their GenAI attributes.
5. **Triage, then the retro.** The facilitator has to call tools: `get_timeline` when it opens, `propose_cards` when
   you say "wrap up". Check the reply streams in, drafts appear, and a card can be confirmed. A model without tool
   calling will fail here, which is worth knowing.
6. **A real GitHub pull request.** New assignment → A GitHub pull request → paste a public PR URL
   (`https://github.com/owner/repo/pull/123`). Public repositories need no token (GitHub allows 60 unauthenticated API
   calls an hour, and a fetch makes three). For a private repository the runners need `GitHub__Token`, which the
   AppHost does not pass yet: add a secret parameter and `.WithEnvironment("GitHub__Token", ...)` on the `runner`
   resource in `src/Roster.AppHost/AppHost.cs` (next to `Vault__Key`).
7. **Write it up.** A bullet in HANDOFF section 1 (date, endpoint kind, model, each reviewer's outcome, what broke
   and what was fixed); update the "Run end to end, but only with the fake endpoint" risk in `architecture.md` and
   the README status; drop "compiled only" for whatever has now run.

*Done when:* written up, with any fixes committed.

## 0.1 Make the design doc match the code

Four places in [`architecture.md`](architecture.md) describe something other than what was built. Change the doc,
not the code.

1. **Section 8, resolution order** (the numbered list under "Choosing"). It mentions "the agent's default in that
   scenario" and "the user's tier map", neither of which exists; tier maps live on endpoints. What
   `src/Roster.Platform/Models/ModelRouter.cs` does:
   - endpoint: step override → phase override → assignment choice → the owner's default endpoint → the oldest shared
     endpoint;
   - model: step override → phase override → assignment choice → the endpoint's model for the agent's tier → the
     endpoint's default model.
2. **Section 8, the endpoint table.** `capabilities`: the tool-calling and reasoning flags are stored but nothing reads
   them yet, and there is no embeddings field (slice 2's K2 adds one). `limits`: only max concurrency is enforced,
   per runner process (`src/Roster.Agents.Runtime/EndpointGates.cs`); requests per minute is stored, not enforced.
3. **Section 11, the facilitator's tools.** The doc lists `get_overview`, `get_decisions` and `get_tool_calls`. Built
   (in `src/Roster.Platform/Tools/AssignmentTools.cs` and `RetroTools.cs`): `get_timeline`, `list_steps`,
   `get_step(stepId)` (input, output, tool calls, tokens), `get_findings`, `get_reasoning(stepId)`,
   `get_chat(stepId)`, and `propose_cards`.
4. **Section 11, scorecards.** The doc promises recurring phrases, rework loops and cost. Built
   (`src/Roster.Platform/Retro/ScorecardService.cs`): calls (succeeded, repaired, failed), tokens in and out, average
   time, findings (accepted, rejected, undecided), acceptance rate, Good / Bad / Ugly, and all of it by the author's
   title. Move recurring phrases to slice 2 (the lesson curator), rework loops to slice 3 (the scenario with loops),
   and cost to "when there is a price table".

*Done when:* the four places match the code. One commit.

## 0.2 Clear stored reasoning after a set time

The design doc (sections 5 and 16) says stored reasoning is kept for a limited time. Nothing clears it.

1. **A column.** Add `ReasoningClearedAt` (`DateTimeOffset?`) to `Invocation` in `src/Roster.Platform/Data/Ledger.cs`,
   then `dotnet ef migrations add ReasoningRetention --project src/Roster.Platform`. The migrator applies it on start.
2. **A service in the runner.** `src/Roster.Runner/ReasoningRetentionService.cs`, a `BackgroundService` registered in
   `Program.cs` beside `RunnerService`. Settings `Ledger:ReasoningRetentionDays` (default 30) and
   `Ledger:RetentionIntervalMinutes` (default 60). Each tick is one `ExecuteUpdateAsync`: for invocations with
   reasoning that started before the cutoff, set `Reasoning` to null and `ReasoningClearedAt` to now. Running it on
   every runner replica is harmless because it is idempotent. Log how many rows it cleared.
3. **Say so where reasoning is shown.** The step view (`StepDetail` in `src/Roster.Api/Endpoints/AssignmentEndpoints.cs`,
   the ledger in `src/Roster.Web/src/components/LedgerView.tsx`) shows "Reasoning cleared after N days" instead of
   nothing. The facilitator's `get_reasoning` says it was cleared, not "not captured", so it does not mistake an old
   step for one that never had reasoning.

*Done when:* with `Ledger__ReasoningRetentionDays=0` and `Ledger__RetentionIntervalMinutes=1` on the runner (a
temporary `.WithEnvironment(...)` on the `runner` resource in `AppHost.cs`), a run on the fake endpoint, which does
return reasoning, loses it within a couple of minutes and the ledger says why. Remove the temporary settings after.

## 0.4 Small UI gaps (optional)

1. **Editing an endpoint.** The API has `PUT /api/endpoints/{id}`; the UI only adds and deletes. Turn
   `AddEndpointForm` in `src/Roster.Web/src/components/settings/EndpointsCard.tsx` into an `EndpointForm` that can
   start from an existing endpoint, and add "Edit" next to "Delete" on the rows you may change. `PUT` replaces every
   field, so send the endpoint's current values for the fields the form does not show (tools, streaming, reasoning
   model, requests per minute).
2. **Admins see everyone's assignments.** `GET /api/assignments?all=true` already works for admins, but the summary has
   only `OwnerId`. Add the owner's name to `AssignmentSummary` in `src/Roster.Api/Endpoints/AssignmentEndpoints.cs`
   (from the users table), and give admins a "Mine / Everyone's" switch on the assignments page, with the switch in
   the query key.

*Done when:* both work in the browser, as an admin and as a member.

## When all four are done

Mark track 0 done in `slice-2.md`, update the HANDOFF and the README status, and merge to the default branch.
