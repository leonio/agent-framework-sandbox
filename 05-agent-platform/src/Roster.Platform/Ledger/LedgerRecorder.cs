using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Roster.Agents;
using Roster.Platform.Data;

namespace Roster.Platform.Ledger;

/// <summary>
/// Writes the ledger: the platform's <see cref="IInvocationLedger"/>. The runtime calls <see cref="BeginAsync"/> before
/// an agent call and <see cref="CompleteAsync"/> after it, whatever the outcome.
/// </summary>
/// <remarks>
/// <para>Opening the row before the model call means a call that never finishes (a runner killed mid-call) still
/// leaves a trace: a row with no outcome, which the UI shows as "interrupted".</para>
/// <para>The first time an agent runs with a given content hash, an <see cref="AgentVersion"/> row is added with its
/// instructions and skills, so scorecards can show what each version was told.</para>
/// <para>Like the router, it uses a short-lived context per call because agents run in parallel.</para>
/// </remarks>
public sealed class LedgerRecorder(IDbContextFactory<RosterDb> dbs) : IInvocationLedger
{
    public async Task<Guid> BeginAsync(InvocationStart start, CancellationToken cancellationToken = default)
    {
        await RememberVersionAsync(start.Agent, cancellationToken);

        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        var invocation = new Invocation
        {
            Id = Guid.NewGuid(),
            AssignmentId = start.Context.AssignmentId,
            UserId = start.Context.UserId,
            PhaseKey = start.Context.PhaseKey,
            StepKey = start.Context.StepKey,
            Attempt = start.Context.Attempt,
            AgentName = start.Agent.Name,
            AgentHash = start.Agent.Hash,
            EndpointId = start.Model.EndpointId,
            EndpointName = start.Model.EndpointName,
            Model = start.Model.Model,
            RuntimeKind = start.RuntimeKind,
            OutputStrategy = start.OutputStrategy,
            InputText = start.InputText,
            StartedAt = DateTimeOffset.UtcNow,
        };

        db.Invocations.Add(invocation);
        await db.SaveChangesAsync(cancellationToken);
        return invocation.Id;
    }

    public async Task CompleteAsync(Guid invocationId, InvocationEnd end, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        Invocation invocation = await db.Invocations.SingleAsync(i => i.Id == invocationId, cancellationToken);

        invocation.Outcome = end.Outcome;
        invocation.OutputJson = end.OutputJson;
        invocation.RawText = end.RawText;
        invocation.Error = end.Error;
        invocation.ToolCallsJson = end.ToolCallsJson;
        invocation.Reasoning = end.Reasoning;
        invocation.InputTokens = end.InputTokens;
        invocation.OutputTokens = end.OutputTokens;
        invocation.DurationMs = end.DurationMs;
        invocation.TraceId = end.TraceId;
        invocation.CompletedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Adds the <see cref="AgentVersion"/> row for this hash if it is new. Two runners can meet the same new hash at the
    /// same moment; the unique index on (agent, hash) lets one insert win and the other's duplicate is ignored.
    /// </summary>
    private async Task RememberVersionAsync(AgentDefinition agent, CancellationToken cancellationToken)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        if (await db.AgentVersions.AnyAsync(v => v.AgentName == agent.Name && v.Hash == agent.Hash, cancellationToken))
        {
            return;
        }

        db.AgentVersions.Add(new AgentVersion
        {
            Id = Guid.NewGuid(),
            AgentName = agent.Name,
            Hash = agent.Hash,
            VersionLabel = agent.Manifest.Version,
            Archetype = agent.Manifest.Archetype,
            Instructions = agent.Instructions,
            SkillsJson = JsonSerializer.Serialize(agent.Skills.Select(s => s.Name)),
            Source = "library",
            FirstSeenAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another call recorded the same version first. That is the outcome we wanted.
        }
    }
}
