using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Roster.Agents;
using Roster.Platform.Data;
using Roster.Platform.Queue;

namespace Roster.Platform.Tools;

/// <summary>
/// Turns the capability ids an agent's manifest names into concrete tools, for one call. This is the platform's
/// <see cref="ICapabilityBinder"/>: the library says "<c>assignment.read</c>", and only here is that wired to the
/// database.
/// </summary>
/// <remarks>
/// <para>Slice 1 backs two capabilities, both read-only in risk terms: <c>assignment.read</c> (<see cref="AssignmentTools"/>)
/// and <c>retro.propose</c> (<see cref="RetroTools"/>).</para>
/// <para>Any other id fails the call instead of running the agent without the tool it expects. <c>repo.read</c> and
/// <c>repo.search</c> exist in the catalogue but no slice 1 agent uses them, so nothing backs them yet.</para>
/// </remarks>
public sealed class CapabilityBinder(IDbContextFactory<RosterDb> dbs, EventBus events) : ICapabilityBinder
{
    public IReadOnlyList<AITool> Bind(IReadOnlyCollection<string> capabilityIds, AgentRunContext context)
    {
        var tools = new List<AITool>();
        foreach (string id in capabilityIds)
        {
            switch (id.ToLowerInvariant())
            {
                case CapabilityCatalog.AssignmentRead:
                    tools.AddRange(new AssignmentTools(dbs, context).Create());
                    break;
                case CapabilityCatalog.RetroPropose:
                    tools.Add(new RetroTools(dbs, events, context).Create());
                    break;
                default:
                    throw new NotSupportedException($"This host has no tool for capability '{id}'.");
            }
        }

        return tools;
    }
}
