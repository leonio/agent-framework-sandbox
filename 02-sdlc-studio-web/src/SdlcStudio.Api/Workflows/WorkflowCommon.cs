using Microsoft.Agents.AI.Workflows;
using SdlcStudio.Api.Ai;
using SdlcStudio.Api.Domain;
using SdlcStudio.Api.Orchestration;

namespace SdlcStudio.Api.Workflows;

// =============================================================================================
// How the workflows in this folder are built (read this first)
// =============================================================================================
//
// Each phase of the SDLC is a small Agent Framework Workflow: a directed graph of *executors*
// connected by *edges*. Messages flow along edges; an executor runs when a message it can handle
// arrives. Execution proceeds in "supersteps" (Pregel style): all executors with pending messages
// run, their outputs are delivered, repeat until no messages are left.
//
// Building blocks used in this sample:
//   Executor<TIn, TOut>             one typed step; the return value is sent along outgoing edges
//   Executor + ConfigureProtocol    a step that handles several message types (TaskDispatcher)
//   BindAsExecutor(lambda)          a tiny function step, no class needed (retry adapters)
//   AddEdge                         A -> B
//   AddEdge<T>(condition)           A -> B only when the condition holds (loops and branches)
//   AddFanOutEdge / FanInBarrier    A -> [B, C, D] in parallel, then wait for all -> E (reviews)
//   IWorkflowContext state          ReadStateAsync / QueueStateUpdateAsync (TaskDispatcher)
//   custom WorkflowEvent            StudioProgressEvent, surfaced live in the UI
//   YieldOutputAsync + WithOutputFrom  the workflow's result
//
// WHY typed, custom executors rather than the high-level AgentWorkflowBuilder helpers
// (BuildSequential, BuildConcurrent, Handoff, GroupChat, Magentic)?
// Those helpers pass a growing List<ChatMessage> between agents, which is ideal for conversational
// hand-offs. An SDLC pipeline passes *artifacts* (a spec, a plan, a code change). Typed messages make
// every edge self-documenting and let plain C# code (validators, publishers, git) sit between agents.
// Use the helpers when agents should talk to each other; use typed executors when they produce work items.
//
// WHY are agents created *outside* the workflow and passed into executors?
// Creating an agent reads its instructions from the database. Doing that up front keeps executors free
// of EF Core (they may run in parallel; a DbContext is not thread safe) and makes them unit-testable
// with any AIAgent.
//
// WHY build a new Workflow instance for every run?
// Executor instances can hold per-run state (the review aggregator counts results). A fresh graph per
// run guarantees isolation. ALTERNATIVE: register executor *factories* with BindExecutor(...) and
// build the workflow once; the framework then creates executors per run for you.
// =============================================================================================

/// <summary>Everything an executor needs to know about the run it is working for.</summary>
public sealed record RunContext(Guid RunId, WorkflowPhase Phase, string Project, string WorkspacePath, PromptSnapshot Prompts)
{
    public string SpecsFolder => Path.Combine(WorkspacePath, "specs");
    public string PlanFolder => Path.Combine(WorkspacePath, "plan");
    public string RepoFolder => Path.Combine(WorkspacePath, "repo");
    public string TestsFolder => Path.Combine(WorkspacePath, "test-prompts");

    /// <summary>Renders a prompt; the project name is always available as {{project}}.</summary>
    public string Prompt(string key, params (string Name, string? Value)[] values) =>
        Prompts.Render(key, values.Append((Name: "project", Value: Project)).ToDictionary(v => v.Name, v => v.Value));
}

/// <summary>
/// A custom workflow event. Executors emit these with <c>context.AddEventAsync</c> to report
/// domain-level progress ("3 findings from the QA reviewer"); <see cref="WorkflowRunner"/> forwards
/// them to the activity log and the UI.
/// </summary>
public sealed class StudioProgressEvent(string message) : WorkflowEvent(message)
{
    public string Message => message;
}

/// <summary>Spec documents shared by the design, import, revise and review workflows.</summary>
public sealed record SpecBundle(string FunctionalSpec, string TechnicalDesign);

/// <summary>A story as the planning, develop and testing workflows see it.</summary>
public sealed record StoryInput(int StoryId, string Key, string Title, string UserStory, string AcceptanceCriteria)
{
    public string ToMarkdown() => $"{Key}: {Title}\n{UserStory}\nAcceptance criteria:\n{AcceptanceCriteria}";
}

/// <summary>
/// Runs a workflow with streaming, relays its events to the run's activity log, and returns its output.
/// </summary>
public sealed class WorkflowRunner(RunJournal journal)
{
    public async Task<TOutput> RunAsync<TInput, TOutput>(Workflow workflow, TInput input, RunContext run, CancellationToken ct)
        where TInput : notnull
    {
        // InProcessExecution runs the graph inside this process. ALTERNATIVES: pass a CheckpointManager
        // to persist state after every superstep (resume after a crash or a long human pause), or host
        // agents/workflows out of process (e.g. Azure AI Foundry / Durable Task) for durability and scale.
        await using StreamingRun streaming = await InProcessExecution.RunStreamingAsync(workflow, input, sessionId: run.RunId.ToString(), ct);

        TOutput? output = default;
        var hasOutput = false;

        await foreach (var evt in streaming.WatchStreamAsync(ct))
        {
            switch (evt)
            {
                case ExecutorInvokedEvent e:
                    await journal.WriteAsync(run.RunId, run.Phase, $"Step started: {e.ExecutorId}", "step", ct);
                    break;
                case StudioProgressEvent p:
                    await journal.WriteAsync(run.RunId, run.Phase, p.Message, "info", ct);
                    break;
                case ExecutorFailedEvent f:
                    await journal.WriteAsync(run.RunId, run.Phase, $"Step failed: {f.ExecutorId}: {f.Data?.Message}", "error", ct);
                    break;
                case WorkflowErrorEvent err:
                    throw new InvalidOperationException($"Workflow '{workflow.Name}' failed: {err.Exception?.GetBaseException().Message}", err.Exception);
                case WorkflowOutputEvent o when o.Is<TOutput>(out var value):
                    output = value;
                    hasOutput = true;
                    break;
            }
        }

        return hasOutput ? output! : throw new InvalidOperationException($"Workflow '{workflow.Name}' finished without producing a {typeof(TOutput).Name}.");
    }
}
