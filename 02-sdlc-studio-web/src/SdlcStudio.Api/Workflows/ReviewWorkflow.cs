using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using SdlcStudio.Api.Domain;

namespace SdlcStudio.Api.Workflows;

// =============================================================================================
// Review workflow: fan-out / fan-in.
//
//                     +--> [reviewer-product]      --+
//   ReviewRequest --> [dispatch] --> [reviewer-architecture] --+--> [aggregate] --> ReviewReport
//                     +--> [reviewer-qa]           --+
//
// Three reviewer agents with different "lenses" look at the same spec in parallel; a barrier
// waits for all of them before the aggregator runs.
//
// WHY three narrow reviewers instead of one "review everything" prompt?
// Focused prompts find more issues (each reviewer has one job) and every finding is attributed to
// a lens, so the human can see *who* raised it. Running them in parallel costs no extra wall time.
//
// ALTERNATIVES:
//  - AgentWorkflowBuilder.BuildConcurrent(agents, aggregator): the same shape in one line when the
//    agents exchange chat messages rather than typed results.
//  - Group chat (AgentWorkflowBuilder.CreateGroupChatBuilderWith): reviewers see and debate each
//    other's findings. Richer, but slower and harder to keep on-topic.
// =============================================================================================

public sealed record ReviewRequest(int Round, SpecBundle Specs);
public sealed record LensFindings(string Reviewer, List<ReviewFindingDraft> Findings);
public sealed record ReviewReport(List<LensFindings> Lenses);

public static class ReviewWorkflow
{
    public static Workflow Build(RunContext run, IReadOnlyList<AIAgent> reviewers)
    {
        var dispatch = new ReviewDispatchExecutor();
        var lenses = reviewers.Select(agent => new SpecReviewerExecutor(run, agent)).ToList();
        var aggregate = new ReviewAggregatorExecutor(lenses.Count);

        // ExecutorBinding is the type edges are declared with; an Executor converts to one implicitly.
        List<ExecutorBinding> lensBindings = [.. lenses];

        return new WorkflowBuilder(dispatch)
            .WithName("review")
            .AddFanOutEdge(dispatch, lensBindings)
            .AddFanInBarrierEdge(lensBindings, aggregate)
            .WithOutputFrom(aggregate)
            .Build();
    }
}

/// <summary>Start node. It exists so the fan-out has a single, named source.</summary>
internal sealed class ReviewDispatchExecutor() : Executor<ReviewRequest, ReviewRequest>("dispatch-review")
{
    public override async ValueTask<ReviewRequest> HandleAsync(ReviewRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        await context.AddEventAsync(new StudioProgressEvent($"Review round {message.Round}: sending spec to reviewers in parallel"), ct);
        return message;
    }
}

/// <summary>Agent step using structured output: returns typed findings, not prose.</summary>
internal sealed class SpecReviewerExecutor(RunContext run, AIAgent agent) : Executor<ReviewRequest, LensFindings>(agent.Name!)
{
    public override async ValueTask<LensFindings> HandleAsync(ReviewRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        var prompt = run.Prompt("review-spec",
            ("round", message.Round.ToString()), ("spec", message.Specs.FunctionalSpec), ("design", message.Specs.TechnicalDesign));

        // RunAsync<T>: the framework sends T's JSON schema as the response format and deserializes the reply.
        var response = await agent.RunAsync<ReviewFindings>(prompt, serializerOptions: StudioJson.Options, cancellationToken: ct);

        await context.AddEventAsync(new StudioProgressEvent($"{agent.Name}: {response.Result.Findings.Count} finding(s)"), ct);
        return new LensFindings(agent.Name!, response.Result.Findings);
    }
}

/// <summary>
/// Fan-in target. The barrier delivers each reviewer's message individually once *all* have arrived,
/// so the aggregator collects them and yields when it has the expected number.
/// </summary>
[YieldsOutput(typeof(ReviewReport))]
internal sealed class ReviewAggregatorExecutor(int expected) : Executor<LensFindings>("aggregate-review")
{
    // NOTE: instance state is fine because a new workflow (and aggregator) is built per run. For a
    // checkpointable workflow keep this list in workflow state (context.QueueStateUpdateAsync), as
    // TaskDispatcherExecutor does, so it is captured in checkpoints.
    private readonly List<LensFindings> _received = [];

    public override async ValueTask HandleAsync(LensFindings message, IWorkflowContext context, CancellationToken ct = default)
    {
        _received.Add(message);
        if (_received.Count < expected) return;

        var total = _received.Sum(l => l.Findings.Count);
        await context.AddEventAsync(new StudioProgressEvent($"Review complete: {total} finding(s) waiting for your decision"), ct);
        await context.YieldOutputAsync(new ReviewReport([.. _received.OrderBy(l => l.Reviewer)]), ct);
    }
}
