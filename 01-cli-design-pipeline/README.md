# 01 · CLI design pipeline

A .NET 10 console app that turns a one-line product idea into a small, reviewed, ready-to-push
codebase using **four cooperating agents and two human gates**, built on
[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/overview/agent-framework-overview)
(`Microsoft.Agents.AI` + `Microsoft.Agents.AI.Workflows` 1.23).

```
idea ─▶ grill-me interview ⇄ you ─▶ spec approval (you) ─▶ developer ⇄ tester (rework loop) ─▶ merge-request agent
                                                                                                 ├─ create_pull_request (MCP / mock)
                                                                                                 ├─ Jira comment + transition (REST)
                                                                                                 └─ Confluence page update (REST)
```

It runs **fully offline by default**: a scripted `IChatClient` stands in for the model, while
everything else (agents, structured output, real tool calls, the workflow graph, HTTP clients)
runs for real. Point it at OpenAI / Azure OpenAI / GitHub Models with one setting.

The code is deliberately over-commented: each file explains *why* it is written that way, the
alternatives that were considered, and links to docs. Suggested reading order:
`Program.cs` → `Workflow/DesignPipelineWorkflow.cs` → `Workflow/Executors/*` →
`Agents/PipelineAgentFactory.cs` → `Tools/*` → `Rag/*`.

## Run it

Requires the .NET 10 SDK.

```bash
cd 01-cli-design-pipeline

# Offline, answers its own questions (first suggestion) and approves the spec:
dotnet run --project src/CliDesignPipeline -- "A tiny task tracker for my team" --auto

# Offline, interactive: you answer the interview and approve/reject the spec.
# Type a number to pick a suggestion, free text, or /done to end the interview early.
dotnet run --project src/CliDesignPipeline -- "A tiny task tracker for my team"
```

With a real model:

```bash
dotnet user-secrets set Pipeline:ApiKey <key> --project src/CliDesignPipeline

dotnet run --project src/CliDesignPipeline -- "..." --Pipeline:Provider=OpenAI
dotnet run --project src/CliDesignPipeline -- "..." --Pipeline:Provider=GitHubModels --Pipeline:Model=openai/gpt-4.1-mini
dotnet run --project src/CliDesignPipeline -- "..." --Pipeline:Provider=AzureOpenAI \
    --Pipeline:Endpoint=https://<resource>.openai.azure.com/openai/v1/ --Pipeline:Model=<deployment>
```

Every run writes a folder under `output/` (git-ignored):

| File | Written by | What it is |
| --- | --- | --- |
| `SPEC.md` | interviewer | The spec you approved |
| `decisions.jsonl` | interviewer | Every human approve/reject **with the reason**, next to the AI output it shaped |
| `app/` | developer + tester | The generated app, plus `tests/` (tester can only write there) |
| `REVIEW-<n>.md` | tester | One per review round: verdict, findings, tests written |
| `MERGE_REQUEST.md` | merge-request agent | MR title/body + the exact git/gh commands to push it yourself |
| `workflow.mmd` | runner | Mermaid diagram generated from the workflow graph |

In mock mode the generated app is always the same small task tracker, and it really builds:
`cd output/<run>/app && dotnet test tests/TaskTracker.Tests` passes (5 tests).

## What it demonstrates

| Agent Framework / .NET AI concept | Where |
| --- | --- |
| `ChatClientAgent` built from prompt files + tools | `Agents/PipelineAgentFactory.cs` |
| Structured output (`agent.RunAsync<T>`) as the contract between agents | `Contracts/AgentOutputs.cs`, every executor |
| `AgentSession` for multi-turn memory (interview, developer rework) vs fresh sessions (tester) | `Workflow/Executors/*` |
| Graph workflow with `WorkflowBuilder`, typed + conditional edges, a bounded **loop** | `Workflow/DesignPipelineWorkflow.cs` |
| Custom executors: multi-type (`Executor` + `ConfigureProtocol`), single-type, and `Executor<TIn,TOut>` auto-yielding output | `Workflow/Executors/*` |
| **Human-in-the-loop** with `RequestPort` + `RequestInfoEvent` / `SendResponseAsync` | interviewer, `Workflow/PipelineRunner.cs`, `Cli/Stakeholders.cs` |
| Shared workflow state (`QueueStateUpdateAsync` / `ReadStateAsync`) | `Workflow/PipelineEvents.cs` (`PipelineState`) |
| Custom workflow events for progress | `StageEvent` |
| Function tools from C# methods (`AIFunctionFactory`), sandboxed, least-privilege per agent | `Tools/WorkspaceTools.cs` |
| Tool decorator (`DelegatingAIFunction`) for observability | `Tools/ObservedFunction.cs` |
| Tools over real REST APIs (Jira v3, Confluence v2) via typed `HttpClient`, mocked at the HTTP handler | `Tools/Integrations/*`, `Tools/TrackerTools.cs` |
| **MCP** client (GitHub MCP server), compiled out by default and mocked | `Tools/GitHubTools.cs` (`dotnet build -p:EnableMcp=true` to enable) |
| **RAG** via `AIContextProvider` (keyword retrieval over `instructions/`, with the vector-store version sketched) | `Rag/InstructionsContextProvider.cs` |
| Provider-neutral `IChatClient` + middleware; offline scripted client | `Ai/ChatClientFactory.cs`, `Ai/Mock/*` |
| Options pattern, user-secrets, `IHttpClientFactory` via the generic host | `Program.cs`, `Configuration/PipelineOptions.cs` |

### Modern C# used (C# 14 / .NET 10)

- **Extension members** (`extension(T) { ... }` blocks, including an extension *property*): `Contracts/MarkdownRendering.cs`
- **`field` keyword** for validated auto-properties: `Configuration/PipelineOptions.cs`
- Primary constructors on every service and executor, records for all contracts, collection
  expressions and spreads (`[.. a, .. b]`), list patterns (`args is [var first, ..]`,
  `Findings is []`), property patterns in `switch`, raw string literals, `System.Threading.Lock`,
  `FrozenDictionary`, source-generated `Regex`, `.slnx` solution, central package management.

## The `prompts/`, `skills/` and `instructions/` folders

All agent behaviour that is not plumbing lives in Markdown so non-developers can own it.

| Folder | Granularity | Question it answers | How it reaches the model |
| --- | --- | --- | --- |
| `prompts/` | one per agent | *Who am I, what do I do, what do I return?* | Agent's system instructions. Front matter: `skills`, pinned `instructions`, `temperature`, `{{placeholders}}` |
| `skills/<name>/SKILL.md` | reusable procedure | *How do I do this kind of task?* (interview technique, review checklist, git conventions) | Appended to the instructions of any agent that lists it. Same layout as Claude / Copilot agent skills, so they are portable |
| `instructions/` | org-wide standing rules | *What rules apply here?* (coding standards, definition of done, branching) | Either **pinned** in full, or **retrieved** per call by the RAG provider (only the relevant `##` sections) |

Edit any of them and re-run; no rebuild needed beyond the copy-to-output step `dotnet run` does.

## Design decisions (and the roads not taken)

- **Explicit graph, not a prebuilt orchestration.** `AgentWorkflowBuilder.BuildSequential`,
  handoff, group chat and Magentic exist, but none gives a review loop with a code-enforced
  round limit *plus* typed approval gates, and in an SDLC pipeline step order is policy. See
  `DesignPipelineWorkflow.cs` for when to pick each alternative.
- **Agents wrapped in executors** rather than bound directly, because each step has logic
  (budgets, routing, writing artefacts) and typed inputs/outputs.
- **The model gives a verdict; code decides the route** (`TesterExecutor` maps
  `ReviewVerdict` + round budget to `NextStep`).
- **Hard limits in code, not prompts** (question budget, review rounds, path sandbox,
  tester can only write `tests/`).
- **Mock at the seams that keep the most real code running**: `IChatClient` for the model,
  `HttpMessageHandler` for Jira/Confluence, same-shaped local function for the MCP tool.
- **No checkpointing** to keep the sample short. Executors keep sessions in fields, which is
  fine in-process; the comments in `RequirementsInterviewExecutor` say what to change before
  turning on `CheckpointManager` (sample 02 persists runs to a database).

## Where it could go next

- `ApprovalRequiredAIFunction` on the Jira/Confluence tools so the human approves side effects.
- `.UseOpenTelemetry()` on the chat clients + `.WithOpenTelemetry()` on the workflow, viewed in the Aspire dashboard.
- Replace keyword retrieval with `Microsoft.Extensions.VectorData` + embeddings, and feed past
  review findings back in as a "lessons learned" store.
- A tester that actually runs `dotnet test` in a container via an MCP server and reports real results.
