using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Roster.Agents;
using Roster.Agents.Library;
using Roster.Platform.Data;
using Roster.Platform.Queue;
using Roster.Platform.Sources;

namespace Roster.Platform.Scenarios;

/// <summary>
/// Slice 1's scenario: review a pull request with a team of reviewer agents, then let the person triage the findings.
/// </summary>
/// <remarks>
/// <para>Three phases:</para>
/// <list type="number">
/// <item><b>fetch</b>: load the pull request (the bundled fixture or GitHub) and keep it as the phase output.</item>
/// <item><b>review</b>: run every reviewer in the library at once and store their findings. A reviewer is any agent
/// whose contracts are <see cref="ChangeReviewInput"/> in and <see cref="Findings"/> out, so adding a fourth reviewer
/// is adding an <c>AGENT.md</c>, not changing this class.</item>
/// <item><b>triage</b>: wait for the person to accept or reject each finding (with a reason). Finishes at once when
/// there is nothing to decide.</item>
/// </list>
/// <para><b>Parallel agents, separate scopes.</b> Each reviewer runs in its own DI scope with its own runner. The
/// runtime's services are scoped, and nothing that is scoped may be shared between concurrent calls.</para>
/// <para><b>Partial failure.</b> If some reviewers fail and others succeed, the phase succeeds with what it has; the
/// failures are in the ledger and the event feed. Only if every reviewer fails does the phase fail and get retried.</para>
/// </remarks>
public sealed class PrReviewScenario(
    PullRequestSources sources,
    IAgentCatalog catalog,
    IServiceScopeFactory scopes,
    IDbContextFactory<RosterDb> dbs,
    EventBus events,
    ILogger<PrReviewScenario> logger) : IScenario
{
    public const string ScenarioKey = "pr-review";
    public const string Fetch = "fetch";
    public const string Review = "review";
    public const string Triage = "triage";

    public string Key => ScenarioKey;

    public IReadOnlyList<string> Phases { get; } = [Fetch, Review, Triage];

    public Task<PhaseOutcome> RunPhaseAsync(PhaseRun run, CancellationToken cancellationToken) => run.PhaseKey switch
    {
        Fetch => FetchAsync(run, cancellationToken),
        Review => ReviewAsync(run, cancellationToken),
        Triage => TriageAsync(run, cancellationToken),
        _ => throw new InvalidOperationException($"The {ScenarioKey} scenario has no phase '{run.PhaseKey}'."),
    };

    private async Task<PhaseOutcome> FetchAsync(PhaseRun run, CancellationToken cancellationToken)
    {
        PullRequestSnapshot snapshot = await sources.FetchAsync(run.Source, cancellationToken);
        return new PhaseOutcome.Done(JsonSerializer.Serialize(snapshot, ContractJson.Options));
    }

    private async Task<PhaseOutcome> ReviewAsync(PhaseRun run, CancellationToken cancellationToken)
    {
        PullRequestSnapshot snapshot = JsonSerializer.Deserialize<PullRequestSnapshot>(run.Outputs[Fetch]!, ContractJson.Options)!;
        var input = new ChangeReviewInput(snapshot.Reference, snapshot.Title, snapshot.Description, snapshot.Diff, snapshot.Files);

        List<AgentDefinition> reviewers = [.. catalog.All.Where(a => a.InputType == typeof(ChangeReviewInput) && a.OutputType == typeof(Findings))];
        if (reviewers.Count == 0)
        {
            throw new InvalidOperationException("The agent library has no reviewers (ChangeReviewInput in, Findings out).");
        }

        // An earlier attempt may have stored findings before its runner died. Nobody can have decided on them yet
        // (triage comes after this phase), so clear them rather than show duplicates.
        await using (RosterDb db = await dbs.CreateDbContextAsync(cancellationToken))
        {
            await db.Findings.Where(f => f.AssignmentId == run.AssignmentId && f.Decision == FindingDecision.Pending).ExecuteDeleteAsync(cancellationToken);
        }

        ReviewerResult[] results = await Task.WhenAll(reviewers.Select(r => ReviewWithAsync(r, input, run, cancellationToken)));

        if (results.All(r => r.Error is not null))
        {
            throw new InvalidOperationException("Every reviewer failed: " + string.Join("; ", results.Select(r => $"{r.Agent}: {r.Error}")));
        }

        return new PhaseOutcome.Done(JsonSerializer.Serialize(new { reviewers = results }, ContractJson.Options));
    }

    /// <summary>Runs one reviewer in its own scope, stores its findings and reports progress on the event feed.</summary>
    private async Task<ReviewerResult> ReviewWithAsync(AgentDefinition reviewer, ChangeReviewInput input, PhaseRun run, CancellationToken cancellationToken)
    {
        await events.PublishAsync(run.AssignmentId, EventKinds.AgentStarted, new { phase = Review, agent = reviewer.Name }, cancellationToken);

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IAgentRunner runner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
        var context = new AgentRunContext(run.AssignmentId, run.OwnerId, Review, reviewer.Name, run.Attempt);

        try
        {
            AgentResult<Findings> result = await runner.RunAsync<ChangeReviewInput, Findings>(reviewer.Name, input, context, cancellationToken);
            int stored = await StoreFindingsAsync(reviewer, result, run.AssignmentId, cancellationToken);

            await events.PublishAsync(run.AssignmentId, EventKinds.FindingsAdded, new { agent = reviewer.Name, count = stored }, cancellationToken);
            await events.PublishAsync(run.AssignmentId, EventKinds.AgentCompleted,
                new { phase = Review, agent = reviewer.Name, outcome = "succeeded", findings = stored, invocationId = result.InvocationId }, cancellationToken);
            return new ReviewerResult(reviewer.Name, result.InvocationId, stored, Error: null);
        }
        catch (AgentRunException ex)
        {
            logger.LogWarning("Reviewer {Agent} failed on {AssignmentId}: {Error}", reviewer.Name, run.AssignmentId, ex.Message);
            await events.PublishAsync(run.AssignmentId, EventKinds.AgentCompleted,
                new { phase = Review, agent = reviewer.Name, outcome = "failed", error = ex.Message, invocationId = ex.InvocationId }, cancellationToken);
            return new ReviewerResult(reviewer.Name, ex.InvocationId, 0, ex.Message);
        }
    }

    private async Task<int> StoreFindingsAsync(AgentDefinition reviewer, AgentResult<Findings> result, Guid assignmentId, CancellationToken cancellationToken)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.Findings.AddRange(result.Output.Items.Select((item, rank) => new Finding
        {
            Id = Guid.NewGuid(),
            AssignmentId = assignmentId,
            InvocationId = result.InvocationId,
            AgentName = reviewer.Name,
            AgentHash = reviewer.Hash,
            Rank = rank,
            Title = item.Title,
            Detail = item.Detail,
            Recommendation = item.Recommendation,
            Severity = item.Severity.ToString().ToLowerInvariant(),
            FilePath = item.FilePath,
            Confidence = Math.Clamp(item.Confidence, 0, 1),
            Decision = FindingDecision.Pending,
            CreatedAt = now,
        }));

        return await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<PhaseOutcome> TriageAsync(PhaseRun run, CancellationToken cancellationToken)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        bool anythingToDecide = await db.Findings.AnyAsync(f => f.AssignmentId == run.AssignmentId && f.Decision == FindingDecision.Pending, cancellationToken);
        return anythingToDecide ? new PhaseOutcome.WaitingForPerson() : new PhaseOutcome.Done(null);
    }

    /// <summary>One reviewer's part of the review phase output.</summary>
    private sealed record ReviewerResult(string Agent, Guid InvocationId, int Findings, string? Error);
}
