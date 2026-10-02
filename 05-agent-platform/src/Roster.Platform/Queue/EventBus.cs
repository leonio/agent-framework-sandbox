using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Roster.Agents;
using Roster.Platform.Data;

namespace Roster.Platform.Queue;

/// <summary>The kinds of <see cref="RunEvent"/> the platform publishes. The web app reacts to them by refetching.</summary>
public static class EventKinds
{
    public const string AssignmentUpdated = "assignment.updated";
    public const string PhaseStarted = "phase.started";
    public const string PhaseCompleted = "phase.completed";
    public const string PhaseFailed = "phase.failed";
    public const string AgentStarted = "agent.started";
    public const string AgentCompleted = "agent.completed";
    public const string FindingsAdded = "findings.added";
    public const string FindingDecided = "finding.decided";
    public const string RetroMessage = "retro.message";
    public const string RetroCards = "retro.cards";
}

/// <summary>
/// Publishes what happens in an assignment so browsers watching it update live. The other half, on the API side, is
/// <see cref="EventStream"/>.
/// </summary>
/// <remarks>
/// <para>Publishing is two steps: insert a <see cref="RunEvent"/> row (EF Core), then send a Postgres <c>NOTIFY</c> on the
/// <see cref="Channel"/> channel with the assignment id. The row comes first, so a listener that hears the
/// notification always finds it. The notification carries only the id: listeners read the new rows themselves, which
/// also means a missed notification costs nothing but a short delay (listeners re-check periodically).</para>
/// <para><c>NOTIFY</c> is sent with <c>pg_notify()</c> through <c>ExecuteSql</c>; EF Core has no LINQ for it.</para>
/// <para>Payloads carry ids and small facts, never secrets or long text: the browser refetches what it shows.</para>
/// </remarks>
public sealed class EventBus(IDbContextFactory<RosterDb> dbs)
{
    /// <summary>The Postgres notification channel.</summary>
    public const string Channel = "roster_events";

    public async Task<long> PublishAsync(Guid assignmentId, string kind, object? payload = null, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        var runEvent = new RunEvent
        {
            AssignmentId = assignmentId,
            Kind = kind,
            PayloadJson = payload is null ? "{}" : JsonSerializer.Serialize(payload, ContractJson.Options),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.RunEvents.Add(runEvent);
        await db.SaveChangesAsync(cancellationToken);

        // Interpolated values become parameters: pg_notify('roster_events', '<assignment id>').
        string message = assignmentId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_notify({Channel}, {message})", cancellationToken);
        return runEvent.Id;
    }
}
