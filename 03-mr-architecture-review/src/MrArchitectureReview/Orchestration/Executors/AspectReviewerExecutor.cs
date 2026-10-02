using System.Diagnostics;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using MrArchitectureReview.Domain;

namespace MrArchitectureReview.Orchestration.Executors;

/// <summary>
/// Step 2 (AI, x3 in parallel): one executor instance per <see cref="ReviewAspect"/>, each wrapping its own
/// small agent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Structured output.</b> <c>agent.RunAsync&lt;AspectFindings&gt;(...)</c> asks the model for JSON matching
/// the <see cref="AspectFindings"/> schema (OpenAI "structured outputs" / JSON schema response format) and
/// deserialises it. Typed results are what make the rest of the workflow ordinary C#: no regex over
/// prose, no "please answer in this format" prompt engineering.
/// </para>
/// <para>
/// <b>Why not just put the agent itself in the graph?</b> You can: <c>WorkflowBuilder</c> accepts an
/// <c>AIAgent</c> directly as a node, which then speaks <c>ChatMessage</c>s (that is what
/// 07.Workflow samples 01-03 in the Agent-Framework-Samples repo do). Wrapping it in a typed executor lets us build the prompt from a
/// <see cref="PullRequestSnapshot"/>, get typed output, time the call and degrade gracefully on failure.
/// </para>
/// <para>
/// <b>Failure policy.</b> If the model call fails or returns unusable JSON, we report an empty review
/// with the error in the summary instead of failing the workflow. One flaky reviewer should not throw
/// away two good reviews. The fan-in barrier downstream also waits for <i>every</i> reviewer, so
/// always sending a message keeps the workflow from stalling.
/// Alternative: let it throw and rely on a retry policy (e.g. Polly in the IChatClient pipeline).
/// </para>
/// </remarks>
public sealed class AspectReviewerExecutor(
    ReviewAspect aspect,
    ReviewerAgentFactory agents,
    int maxDiffChars,
    double minimumConfidence,
    ILogger<AspectReviewerExecutor> logger)
    : Executor<PullRequestSnapshot, AspectReview>(aspect.AgentName)
{
    public override async ValueTask<AspectReview> HandleAsync(
        PullRequestSnapshot snapshot, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var (agent, prompt) = agents.CreateReviewer(aspect, snapshot);
        var stopwatch = Stopwatch.StartNew();
        await context.AddEventAsync(new ReviewProgressEvent($"{aspect} reviewer started (prompt {prompt.Sha})"), cancellationToken);

        try
        {
            var response = await agent.RunAsync<AspectFindings>(
                ReviewerAgentFactory.BuildReviewMessage(snapshot, maxDiffChars),
                cancellationToken: cancellationToken);

            var result = response.Result;
            // Ids are assigned here, by the workflow, in the order the model ranked them.
            // Low-confidence findings are dropped before a human sees them: reviewer fatigue is the main
            // reason AI review tools get switched off, so precision matters more than recall here.
            List<Finding> findings = [.. (result.Findings ?? [])
                .Select((draft, i) => Finding.From(draft, aspect, i + 1))
                .Where(f => f.Confidence >= minimumConfidence)];

            await context.AddEventAsync(new ReviewProgressEvent(
                $"{aspect} reviewer finished: {findings.Count} finding(s) in {stopwatch.Elapsed.TotalSeconds:0.0}s"), cancellationToken);

            return new AspectReview(aspect, result.Summary, findings, prompt.Sha, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Aspect} reviewer failed", aspect);
            await context.AddEventAsync(new ReviewProgressEvent($"{aspect} reviewer FAILED: {ex.Message}"), cancellationToken);
            return new AspectReview(aspect, $"Reviewer failed: {ex.Message}", [], prompt.Sha, stopwatch.Elapsed);
        }
    }
}
