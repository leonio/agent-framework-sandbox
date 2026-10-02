using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Roster.Platform.Data;
using Roster.Platform.Queue;

namespace Roster.Platform.Scenarios;

/// <summary>
/// A scenario: a named list of phases and the code that runs each one. Phases are plain C#, persisted by the engine
/// (design doc D6); a phase may use Agent Framework workflows inside it when they earn their place.
/// </summary>
public interface IScenario
{
    /// <summary>The key stored on <see cref="Assignment.Scenario"/>, such as <c>pr-review</c>.</summary>
    string Key { get; }

    /// <summary>The phase keys, in order.</summary>
    IReadOnlyList<string> Phases { get; }

    /// <summary>
    /// Runs one phase. May run more than once for the same phase (a retry, or a job claimed again after a runner died),
    /// so it must tolerate leftovers from an earlier attempt.
    /// </summary>
    Task<PhaseOutcome> RunPhaseAsync(PhaseRun run, CancellationToken cancellationToken);
}

/// <summary>What a phase is given: which assignment, which phase and attempt, and the earlier phases' outputs by key.</summary>
public sealed record PhaseRun(
    Guid AssignmentId,
    string OwnerId,
    string Source,
    string PhaseKey,
    int Attempt,
    IReadOnlyDictionary<string, string?> Outputs);

/// <summary>How a phase run ended, when it did not throw.</summary>
public abstract record PhaseOutcome
{
    /// <summary>The phase is finished. Its output (JSON, may be null) is kept for later phases and the next phase is queued.</summary>
    public sealed record Done(string? OutputJson) : PhaseOutcome;

    /// <summary>
    /// The phase now waits for the person (triage). The assignment shows as awaiting them, and the phase finishes when
    /// the platform calls <see cref="ScenarioEngine.CompleteWaitingPhaseAsync"/>.
    /// </summary>
    public sealed record WaitingForPerson : PhaseOutcome;
}

/// <summary>A request to start an assignment, as the API receives it.</summary>
public sealed record NewAssignment(
    string OwnerId,
    string Scenario,
    string Title,
    string Source,
    Guid? EndpointId = null,
    string? Model = null,
    string OverridesJson = "{}");

/// <summary>
/// Moves assignments through their scenario's phases. The API calls <see cref="CreateAssignmentAsync"/>; runners call
/// <see cref="RunPhaseAsync"/> for each <c>phase.run</c> job.
/// </summary>
/// <remarks>
/// <para>One phase per job. When a phase finishes, the engine queues a job for the next one in the same transaction
/// that records the result, so a phase never finishes without its successor being queued (or vice versa).</para>
/// <para>Failures: the phase is marked failed and the exception goes back to the runner, which fails the job; the
/// queue retries it with back-off. Only on the last attempt does the assignment itself become failed.</para>
/// <para>Repeats: a job for a phase that already succeeded does nothing, so a job that runs twice is harmless.</para>
/// <para>Cancellation is a flag the person sets. It takes effect at the start of the next phase, or straight away if
/// nothing is running (a queued assignment, or one waiting for triage).</para>
/// </remarks>
public sealed class ScenarioEngine(
    IDbContextFactory<RosterDb> dbs,
    IEnumerable<IScenario> scenarios,
    IJobQueue jobs,
    EventBus events,
    ILogger<ScenarioEngine> logger)
{
    public IScenario Find(string key) =>
        scenarios.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new NotSupportedException($"Unknown scenario '{key}'.");

    /// <summary>Creates the assignment and its phases, and queues the first phase, all in one transaction.</summary>
    public async Task<Assignment> CreateAssignmentAsync(NewAssignment request, CancellationToken cancellationToken = default)
    {
        IScenario scenario = Find(request.Scenario);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var assignment = new Assignment
        {
            Id = Guid.NewGuid(),
            OwnerId = request.OwnerId,
            Scenario = scenario.Key,
            Title = request.Title,
            Source = request.Source,
            State = AssignmentState.Queued,
            EndpointId = request.EndpointId,
            Model = request.Model,
            OverridesJson = request.OverridesJson,
            CreatedAt = now,
            UpdatedAt = now,
            Phases = [.. scenario.Phases.Select((key, i) => new Phase
            {
                Id = Guid.NewGuid(),
                Key = key,
                Order = i,
                State = i == 0 ? PhaseState.Queued : PhaseState.Pending,
            })],
        };

        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        db.Assignments.Add(assignment);
        jobs.Add(db, JobKinds.PhaseRun, new PhaseRunPayload(assignment.Id, assignment.Phases[0].Id));
        await db.SaveChangesAsync(cancellationToken);

        await events.PublishAsync(assignment.Id, EventKinds.AssignmentUpdated, new { state = assignment.State }, cancellationToken);
        logger.LogInformation("Created {Scenario} assignment {AssignmentId} for {Owner}", scenario.Key, assignment.Id, request.OwnerId);
        return assignment;
    }

    /// <summary>Runs the phase a <c>phase.run</c> job names. <paramref name="lastAttempt"/> comes from the job.</summary>
    public async Task RunPhaseAsync(PhaseRunPayload payload, bool lastAttempt, CancellationToken cancellationToken)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        Assignment assignment = await db.Assignments.Include(a => a.Phases).SingleAsync(a => a.Id == payload.AssignmentId, cancellationToken);
        Phase phase = assignment.Phases.Single(p => p.Id == payload.PhaseId);

        if (phase.State == PhaseState.Succeeded || assignment.State is AssignmentState.Completed or AssignmentState.Failed or AssignmentState.Cancelled)
        {
            logger.LogInformation("Phase {Phase} of {AssignmentId} needs no run (phase {PhaseState}, assignment {State})",
                phase.Key, assignment.Id, phase.State, assignment.State);
            return;
        }

        if (assignment.CancelRequested)
        {
            await CancelAsync(db, assignment, cancellationToken);
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        phase.State = PhaseState.Running;
        phase.Attempt++;
        phase.StartedAt = now;
        phase.Error = null;
        assignment.State = AssignmentState.Running;
        assignment.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(assignment.Id, EventKinds.PhaseStarted, new { phase = phase.Key, attempt = phase.Attempt }, cancellationToken);

        var run = new PhaseRun(
            assignment.Id,
            assignment.OwnerId,
            assignment.Source,
            phase.Key,
            phase.Attempt,
            assignment.Phases.Where(p => p.Order < phase.Order).ToDictionary(p => p.Key, p => p.OutputJson));

        PhaseOutcome outcome;
        try
        {
            outcome = await Find(assignment.Scenario).RunPhaseAsync(run, cancellationToken);
        }
        catch (Exception ex)
        {
            // Record the failure even when the runner is shutting down (CancellationToken.None), then let the job fail.
            phase.State = PhaseState.Failed;
            phase.Error = ex.Message;
            if (lastAttempt)
            {
                assignment.State = AssignmentState.Failed;
                assignment.Error = $"{phase.Key}: {ex.Message}";
                assignment.CompletedAt = DateTimeOffset.UtcNow;
            }

            assignment.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await events.PublishAsync(assignment.Id, EventKinds.PhaseFailed,
                new { phase = phase.Key, attempt = phase.Attempt, error = ex.Message, willRetry = !lastAttempt }, CancellationToken.None);
            throw;
        }

        if (outcome is PhaseOutcome.WaitingForPerson)
        {
            assignment.State = AssignmentState.AwaitingTriage;
            assignment.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await events.PublishAsync(assignment.Id, EventKinds.AssignmentUpdated, new { state = assignment.State, phase = phase.Key }, cancellationToken);
            return;
        }

        await FinishPhaseAsync(db, assignment, phase, ((PhaseOutcome.Done)outcome).OutputJson, cancellationToken);
    }

    /// <summary>
    /// Finishes a phase that was waiting for the person (triage, once every finding has a decision) and moves on.
    /// </summary>
    public async Task CompleteWaitingPhaseAsync(Guid assignmentId, string phaseKey, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        Assignment assignment = await db.Assignments.Include(a => a.Phases).SingleAsync(a => a.Id == assignmentId, cancellationToken);
        Phase phase = assignment.Phases.Single(p => p.Key == phaseKey);
        if (assignment.State != AssignmentState.AwaitingTriage || phase.State != PhaseState.Running)
        {
            return;
        }

        await FinishPhaseAsync(db, assignment, phase, outputJson: null, cancellationToken);
    }

    /// <summary>
    /// Asks for the assignment to stop. If nothing is running it stops now; otherwise the runner stops it before its
    /// next phase. Returns false if the person does not own it.
    /// </summary>
    public async Task<bool> RequestCancelAsync(Guid assignmentId, string userId, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        Assignment? assignment = await db.Assignments.Include(a => a.Phases)
            .SingleOrDefaultAsync(a => a.Id == assignmentId && a.OwnerId == userId, cancellationToken);
        if (assignment is null)
        {
            return false;
        }

        assignment.CancelRequested = true;
        if (assignment.State is AssignmentState.AwaitingTriage or AssignmentState.Queued)
        {
            await CancelAsync(db, assignment, cancellationToken);
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    // Marks the phase done, then queues the next phase or completes the assignment, in one save.
    private async Task FinishPhaseAsync(RosterDb db, Assignment assignment, Phase phase, string? outputJson, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        phase.State = PhaseState.Succeeded;
        phase.OutputJson = outputJson;
        phase.CompletedAt = now;
        assignment.UpdatedAt = now;

        Phase? next = assignment.Phases.Where(p => p.Order > phase.Order).OrderBy(p => p.Order).FirstOrDefault();
        if (next is not null)
        {
            next.State = PhaseState.Queued;
            assignment.State = AssignmentState.Queued;
            jobs.Add(db, JobKinds.PhaseRun, new PhaseRunPayload(assignment.Id, next.Id));
        }
        else
        {
            assignment.State = AssignmentState.Completed;
            assignment.CompletedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(assignment.Id, EventKinds.PhaseCompleted, new { phase = phase.Key, next = next?.Key, state = assignment.State }, cancellationToken);
    }

    private async Task CancelAsync(RosterDb db, Assignment assignment, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (Phase p in assignment.Phases.Where(p => p.State is PhaseState.Pending or PhaseState.Queued or PhaseState.Running))
        {
            p.State = PhaseState.Cancelled;
        }

        assignment.State = AssignmentState.Cancelled;
        assignment.CompletedAt = now;
        assignment.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(assignment.Id, EventKinds.AssignmentUpdated, new { state = assignment.State }, cancellationToken);
        logger.LogInformation("Assignment {AssignmentId} cancelled", assignment.Id);
    }
}
