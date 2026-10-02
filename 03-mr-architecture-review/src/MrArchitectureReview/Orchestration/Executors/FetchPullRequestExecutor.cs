using Microsoft.Agents.AI.Workflows;
using MrArchitectureReview.Domain;
using MrArchitectureReview.Sources;

namespace MrArchitectureReview.Orchestration.Executors;

/// <summary>
/// Step 1 (no AI): resolve the request to a PR source and take a snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Executors vs agents.</b> In the Agent Framework, an <i>executor</i> is any node in the workflow graph.
/// Some wrap an AI agent, many don't. Plain-code executors like this one are where determinism, I/O and
/// validation belong. Mixing both freely in one graph is the main reason to use a workflow at all,
/// rather than one big agent with lots of tools.
/// </para>
/// <para>
/// <b>Executor&lt;TIn, TOut&gt;</b> is the simplest way to write a typed executor: one handler, and the return
/// value is automatically sent along the outgoing edges. Alternatives in the same package:
/// <list type="bullet">
///   <item>A lambda: <c>Func&lt;TIn, TOut&gt;.BindAsExecutor("id")</c>, good for one-liners.</item>
///   <item>A class deriving from <c>Executor</c> overriding <c>ConfigureProtocol</c> for several message types
///         (see <see cref="TriageCoordinatorExecutor"/>).</item>
///   <item><c>[MessageHandler]</c> methods on a <c>partial</c> executor class, wired by the
///         Microsoft.Agents.AI.Workflows.Generators source generator (used by 07.Workflow sample 04 in the Agent-Framework-Samples repo).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class FetchPullRequestExecutor(PullRequestSourceResolver sources)
    : Executor<ReviewRequest, PullRequestSnapshot>("fetch-pull-request")
{
    public override async ValueTask<PullRequestSnapshot> HandleAsync(
        ReviewRequest message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await context.AddEventAsync(new ReviewProgressEvent($"Fetching {message.Source} ..."), cancellationToken);

        var snapshot = await sources.Resolve(message).FetchAsync(message, cancellationToken);

        // Make the snapshot and run metadata available to later, non-adjacent executors.
        var run = new RunInfo(RunId: $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}", StartedAt: DateTimeOffset.UtcNow);
        await context.QueueStateUpdateAsync(StateKeys.Snapshot, snapshot, StateKeys.Scope, cancellationToken);
        await context.QueueStateUpdateAsync(StateKeys.RunInfo, run, StateKeys.Scope, cancellationToken);

        await context.AddEventAsync(new ReviewProgressEvent(
            $"{snapshot.RepositoryKey}#{snapshot.Number} \"{snapshot.Title}\": {snapshot.Files.Count} file(s), {snapshot.Diff.Length:N0} chars of diff"),
            cancellationToken);

        return snapshot; // fanned out to the three reviewers by the edge in ReviewWorkflowFactory
    }
}
