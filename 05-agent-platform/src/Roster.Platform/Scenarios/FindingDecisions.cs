using Microsoft.EntityFrameworkCore;
using Roster.Platform.Data;
using Roster.Platform.Queue;

namespace Roster.Platform.Scenarios;

/// <summary>
/// Triage: the person accepts or rejects each finding, with a reason. When the last pending finding is decided, the
/// triage phase finishes and the assignment completes.
/// </summary>
/// <remarks>
/// <para>A rejection needs a reason. "Why was this wrong?" is the question the retro asks and the signal scorecards
/// learn from, so the cheapest moment to capture it is now. An acceptance may have one.</para>
/// <para>The person's name and title are copied onto the finding at the moment of the decision, so a later change of
/// title does not rewrite history.</para>
/// <para>A decision can be changed while the assignment is awaiting triage or after it has completed.</para>
/// </remarks>
public sealed class FindingDecisions(IDbContextFactory<RosterDb> dbs, ScenarioEngine engine, EventBus events)
{
    public async Task<Finding> DecideAsync(
        Guid findingId, string userId, FindingDecision decision, string? reason, CancellationToken cancellationToken = default)
    {
        if (decision == FindingDecision.Pending)
        {
            throw new ArgumentException("A decision is either accepted or rejected.", nameof(decision));
        }

        if (decision == FindingDecision.Rejected && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Say why the finding is wrong or not worth doing; the reason is what improves the agent.", nameof(reason));
        }

        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        Finding finding = await db.Findings.SingleOrDefaultAsync(f => f.Id == findingId, cancellationToken)
            ?? throw new KeyNotFoundException($"No finding {findingId}.");

        Assignment assignment = await db.Assignments.SingleAsync(a => a.Id == finding.AssignmentId, cancellationToken);
        if (assignment.OwnerId != userId)
        {
            throw new UnauthorizedAccessException("Only the assignment's owner can decide on its findings.");
        }

        if (assignment.State is not (AssignmentState.AwaitingTriage or AssignmentState.Completed))
        {
            throw new ConflictException($"Findings can be decided once the review is done; the assignment is {assignment.State}.");
        }

        AppUser? person = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        finding.Decision = decision;
        finding.DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        finding.DecidedById = userId;
        finding.DecidedByName = person?.DisplayName;
        finding.DecidedByTitle = person?.Title;
        finding.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await events.PublishAsync(assignment.Id, EventKinds.FindingDecided, new { findingId, decision }, cancellationToken);

        bool anyPending = await db.Findings.AnyAsync(f => f.AssignmentId == assignment.Id && f.Decision == FindingDecision.Pending, cancellationToken);
        if (!anyPending && assignment.State == AssignmentState.AwaitingTriage)
        {
            await engine.CompleteWaitingPhaseAsync(assignment.Id, PrReviewScenario.Triage, cancellationToken);
        }

        return finding;
    }
}
