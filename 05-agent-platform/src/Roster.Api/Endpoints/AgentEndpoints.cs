using Microsoft.EntityFrameworkCore;
using Roster.Agents;
using Roster.Platform.Data;
using Roster.Platform.Retro;

namespace Roster.Api.Endpoints;

/// <summary>
/// The agent catalogue and scorecards: what agents the library has, what they are allowed to do and where they may
/// run, which versions have run, and how each version has done.
/// </summary>
public static class AgentEndpoints
{
    public sealed record CapabilityView(string Id, string Risk);

    public sealed record AgentSummary(
        string Name,
        string Description,
        string Archetype,
        string VersionLabel,
        string Hash,
        string Tier,
        string Runtime,
        string Placement,
        IReadOnlyList<CapabilityView> Capabilities,
        IReadOnlyList<string> Skills,
        string? Input,
        string? Output,
        AgentScorecard? Scorecard);

    public sealed record AgentVersionView(string Hash, string VersionLabel, string Source, DateTimeOffset FirstSeenAt, bool Current);

    public sealed record AgentDetail(AgentSummary Agent, string Instructions, IReadOnlyList<AgentVersionView> Versions, IReadOnlyList<AgentScorecard> Scorecards);

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder agents = app.MapGroup("/api/agents").WithTags("Agents");

        // Every agent with the scorecard of the version that runs today (its current hash).
        agents.MapGet("/", async (IAgentCatalog catalog, ScorecardService scorecards, CancellationToken cancellationToken) =>
        {
            IReadOnlyList<AgentScorecard> cards = await scorecards.GetAsync(cancellationToken: cancellationToken);
            return catalog.All.Select(a => Summarise(a, cards.FirstOrDefault(c => c.AgentName == a.Name && c.Hash == a.Hash)));
        });

        // One agent: its instructions, every version that has run (newest first) and a scorecard per version.
        agents.MapGet("/{name}", async (string name, IAgentCatalog catalog, ScorecardService scorecards, RosterDb db, CancellationToken cancellationToken) =>
        {
            AgentDefinition agent = catalog.Get(name);
            IReadOnlyList<AgentScorecard> cards = await scorecards.GetAsync(agent.Name, cancellationToken);
            List<AgentVersionView> versions = [.. (await db.AgentVersions.AsNoTracking()
                    .Where(v => v.AgentName == agent.Name)
                    .OrderByDescending(v => v.FirstSeenAt)
                    .ToListAsync(cancellationToken))
                .Select(v => new AgentVersionView(v.Hash, v.VersionLabel, v.Source, v.FirstSeenAt, v.Hash == agent.Hash))];

            return new AgentDetail(Summarise(agent, cards.FirstOrDefault(c => c.Hash == agent.Hash)), agent.Instructions, versions, cards);
        });

        // All scorecards, optionally for one agent: per version, with the feedback split by the giver's title.
        app.MapGet("/api/scorecards", (string? agent, ScorecardService scorecards, CancellationToken cancellationToken) =>
                scorecards.GetAsync(agent, cancellationToken))
            .WithTags("Agents");

        return app;
    }

    private static AgentSummary Summarise(AgentDefinition agent, AgentScorecard? scorecard) => new(
        agent.Name,
        agent.Manifest.Description,
        agent.Manifest.Archetype,
        agent.Manifest.Version,
        agent.Hash,
        agent.Manifest.Tier.ToString().ToLowerInvariant(),
        agent.Manifest.Runtime.ToString().ToLowerInvariant(),
        PlacementPolicy.Minimum(agent.Manifest).ToString(),
        [.. agent.Manifest.Capabilities.Select(c => new CapabilityView(c, CapabilityCatalog.RiskOf(c).ToString()))],
        [.. agent.Skills.Select(s => s.Name)],
        agent.Manifest.Input,
        agent.Manifest.Output,
        scorecard);
}
