using Microsoft.Agents.AI.Workflows;

namespace CliDesignPipeline.Workflow;

/// <summary>
/// A custom workflow event: "stage X says Y". Raised with <c>context.AddEventAsync(...)</c> and
/// received by the host in <c>run.WatchStreamAsync()</c> alongside the built-in events.
/// </summary>
/// <remarks>
/// Custom events are how executors report progress without coupling to any UI. The console
/// prints them; sample 02 would persist them as a run timeline. The built-in alternatives are
/// <c>ExecutorInvokedEvent</c>/<c>ExecutorCompletedEvent</c> (too low level for a user) and
/// <c>AgentResponseUpdateEvent</c> (token streaming, only emitted when an <see cref="Microsoft.Agents.AI.AIAgent"/>
/// is bound directly as an executor - we wrap ours in custom executors instead).
/// </remarks>
public sealed class StageEvent(string stage, string message) : WorkflowEvent(message)
{
    public string Stage { get; } = stage;

    public string Message { get; } = message;
}

/// <summary>
/// Keys for workflow-scoped shared state.
/// </summary>
/// <remarks>
/// <para>
/// Executors communicate in two ways: <b>messages</b> along edges (the hand-off) and <b>shared
/// state</b> (<c>QueueStateUpdateAsync</c> / <c>ReadStateAsync</c>) for facts several steps need.
/// The approved spec is the textbook case: the developer, tester and release agent all need it,
/// and threading it through every message would make each record carry baggage.
/// </para>
/// <para>
/// State updates are <i>queued</i> and become visible at the next super-step boundary (the
/// workflow runs in Pregel-style super-steps). That makes runs deterministic and checkpointable:
/// a checkpoint is taken between super-steps and captures state + in-flight messages.
/// </para>
/// </remarks>
public static class PipelineState
{
    public const string Scope = "pipeline";
    public const string Spec = "approved-spec";
    public const string WorkItemKey = "work-item-key";
}
