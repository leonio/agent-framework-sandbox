using System.Text.Json;
using CliDesignPipeline.Contracts;
using CliDesignPipeline.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace CliDesignPipeline.Workflow.Executors;

/// <summary>
/// Step 4: the release agent opens the (mocked) pull request, updates Jira and Confluence through
/// tools, and returns the exact git commands for a human to push. Its result is the workflow's
/// output.
/// </summary>
/// <remarks>
/// <para>
/// This executor uses the two-type <see cref="Executor{TInput, TOutput}"/>: whatever
/// <see cref="HandleAsync"/> returns is automatically yielded as a workflow output (because the
/// builder registers this executor with <c>WithOutputFrom</c>) - no explicit
/// <c>YieldOutputAsync</c> needed. Compare <see cref="TesterExecutor"/>, which returns nothing
/// and sends explicitly.
/// </para>
/// <para>
/// <b>Why does an agent do this and not plain code?</b> Honestly, for a fixed sequence of four
/// API calls, plain code is more reliable. It is an agent here to show tool-calling against real
/// HTTP APIs and MCP, and because the <i>content</i> (PR body, Jira comment, wiki note) benefits
/// from being written in context. A good hybrid: code does the calls, the model writes the text.
/// </para>
/// </remarks>
internal sealed class MergeRequestExecutor(AIAgent releaseAgent, RunWorkspace workspace)
    : Executor<ReviewOutcome, MergeRequestPlan>("merge-request")
{
    public override async ValueTask<MergeRequestPlan> HandleAsync(ReviewOutcome outcome, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await context.AddEventAsync(new StageEvent("release", "Preparing merge request and updating Jira / Confluence..."), cancellationToken);

        var spec = await context.ReadStateAsync<RequirementsSpec>(PipelineState.Spec, PipelineState.Scope, cancellationToken);
        var workItem = await context.ReadStateAsync<string>(PipelineState.WorkItemKey, PipelineState.Scope, cancellationToken);

        var prompt = $"""
            Work item: {workItem}

            ## Specification
            ```json
            {JsonSerializer.Serialize(spec, PipelineJson.Options)}
            ```

            ## Final review (round {outcome.Round})
            ```json
            {JsonSerializer.Serialize(outcome.Report, PipelineJson.Options)}
            ```

            ## Files
            {string.Join(Environment.NewLine, workspace.ListAppFiles().Select(f => $"- {f}"))}

            The app lives in the `app/` folder of the run directory.
            """;

        var session = await releaseAgent.CreateSessionAsync(cancellationToken);
        var plan = (await releaseAgent.RunAsync<MergeRequestPlan>(prompt, session, PipelineJson.Options, cancellationToken: cancellationToken)).Result;

        if (outcome.Report.Verdict is not ReviewVerdict.Approved)
        {
            // Make the unresolved state impossible to miss, whatever the model wrote.
            plan = plan with { Notes = [$"Shipped after {outcome.Round} round(s) WITHOUT tester approval - review open findings first.", .. plan.Notes] };
        }

        await workspace.WriteArtifactAsync("MERGE_REQUEST.md", plan.ToMarkdown(), cancellationToken);
        await context.AddEventAsync(new StageEvent("release", "Merge request notes saved to MERGE_REQUEST.md"), cancellationToken);
        return plan;
    }
}
