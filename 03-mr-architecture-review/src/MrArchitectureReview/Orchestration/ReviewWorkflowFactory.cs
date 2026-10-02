using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrArchitectureReview.Ai;
using MrArchitectureReview.Configuration;
using MrArchitectureReview.Domain;
using MrArchitectureReview.Persistence;
using MrArchitectureReview.Sources;
using MrArchitectureReview.Orchestration.Executors;

namespace MrArchitectureReview.Orchestration;

/// <summary>
/// Declares the workflow graph. Reading <see cref="Build"/> top to bottom is the best overview of the sample.
/// </summary>
/// <remarks>
/// <code>
///                      ┌─► design-reviewer ────────┐
/// fetch-pull-request ──┼─► security-reviewer ──────┼─► (fan-in) consolidate-findings ─► triage-coordinator ◄──┐
///                      └─► extensibility-reviewer ─┘                                        │                │
///                                                                                           ├─► human-triage ┘ (RequestPort: the human)
///                                                                                           ▼
///                                                                                    record-review ─► publish ─► output
/// </code>
/// <para>
/// <b>WHY a hand-built graph and not one of the prebuilt orchestrations?</b> <c>AgentWorkflowBuilder</c> offers
/// Sequential, Concurrent, Handoff, GroupChat and Magentic patterns over agents that exchange chat messages.
/// They are the right choice when the agents themselves should decide what happens next (handoff,
/// Magentic) or when the whole flow is "pass the transcript along". Here the flow is known in advance
/// and the messages are typed domain objects, so an explicit graph is clearer, cheaper (no "manager"
/// LLM calls deciding routing) and testable. Rule of thumb: <i>deterministic structure, AI at the leaves</i>.
/// </para>
/// <para>
/// <b>Execution model.</b> The engine runs in supersteps: every executor with pending messages runs
/// (concurrently), then all their outputs are delivered for the next superstep. So the three reviewers
/// genuinely run in parallel, and the request port simply leaves a pending request until the host
/// responds.
/// </para>
/// <para>
/// <b>Visualise it:</b> <c>workflow.ToMermaidString()</c> / <c>ToDotString()</c> (the app prints the Mermaid
/// version with <c>--graph</c>).
/// </para>
/// </remarks>
public sealed class ReviewWorkflowFactory(
    PullRequestSourceResolver sources,
    ReviewerAgentFactory agents,
    ReviewStore store,
    ChatClientFactory chatClients,
    IOptions<ReviewOptions> options,
    ILoggerFactory loggerFactory)
{
    /// <summary>The port id the host listens on for human triage requests.</summary>
    public const string HumanTriagePortId = "human-triage";

    /// <summary>Builds a fresh workflow. Call once per run (executors keep per-run state).</summary>
    public Workflow Build()
    {
        var settings = options.Value.Settings;

        var fetch = new FetchPullRequestExecutor(sources);

        // C# 14 / .NET 10 collection expression + Enum.GetValues<T>(): one reviewer per aspect. Adding a
        // fourth aspect (e.g. Performance) = enum value + prompt file + mock file. No graph changes.
        AspectReviewerExecutor[] reviewers = [.. Enum.GetValues<ReviewAspect>().Select(aspect =>
            new AspectReviewerExecutor(aspect, agents, settings.MaxDiffCharacters, settings.MinimumConfidence,
                loggerFactory.CreateLogger<AspectReviewerExecutor>()))];

        var consolidate = new ConsolidateFindingsExecutor(expectedReviews: reviewers.Length);
        var triage = new TriageCoordinatorExecutor(options.Value.Reviewer, chatClients.ProviderName, chatClients.ModelName);
        var humanTriage = RequestPort.Create<TriageRequest, TriageDecision>(HumanTriagePortId);
        var record = new RecordReviewExecutor(store);
        var publish = new PublishExecutor(agents);

        // ExecutorBinding has implicit conversions from Executor, RequestPort and AIAgent, so they can be
        // mixed freely in the builder calls below.
        ExecutorBinding[] reviewerBindings = [.. reviewers];

        return new WorkflowBuilder(fetch)
            .WithName("mr-architecture-review")
            .WithDescription("Surface-level design/security/extensibility review of a pull request with human triage.")
            .AddFanOutEdge(fetch, reviewerBindings)            // 1 -> N, same message to all
            .AddFanInBarrierEdge(reviewerBindings, consolidate) // N -> 1, waits for all
            .AddEdge(consolidate, triage)
            .AddEdge(triage, humanTriage)                       // TriageRequest out to the human...
            .AddEdge(humanTriage, triage)                       // ...TriageDecision back in
            .AddEdge(triage, record)                            // ReviewRecord once all decided
            .AddEdge(record, publish)
            .WithOutputFrom(publish)
            .Build();

        // Note on the two edges leaving `triage`: both receive every message, but each executor only
        // accepts the types it declares. TriageRequest is only routable to the port and ReviewRecord only
        // to `record`, so no conditional edges are needed. Conditional edges
        // (AddEdge<T>(from, to, condition)) are the tool when two targets accept the same type.
    }
}
