namespace Roster.Agents;

/// <summary>
/// What a tool is allowed to do to the world. The highest class an agent uses decides the minimum place it may run.
/// </summary>
public enum RiskClass { Read = 0, WriteLocal = 1, Exec = 2, WriteExternal = 3 }

public sealed record CapabilityInfo(string Id, RiskClass Risk, string Description);

/// <summary>
/// The capabilities an agent can name in its manifest. An agent never references Jira, GitHub or a file system; it names
/// a capability and the host decides which tool backs it. Unknown capability ids are treated as the highest risk, so a
/// typo fails closed.
/// </summary>
public static class CapabilityCatalog
{
    public const string RepoRead = "repo.read";
    public const string RepoSearch = "repo.search";
    public const string AssignmentRead = "assignment.read";
    public const string RetroPropose = "retro.propose";

    private static readonly CapabilityInfo[] s_all =
    [
        new(RepoRead, RiskClass.Read, "Read a file from the snapshot of the change under review."),
        new(RepoSearch, RiskClass.Read, "Search the snapshot of the change under review."),
        new(AssignmentRead, RiskClass.Read, "Read the owner's assignment: steps, outputs, decisions, chats and reasoning."),
        new(RetroPropose, RiskClass.Read, "Hand draft retro cards to the person for review. Nothing is saved without their confirmation."),
    ];

    public static IReadOnlyDictionary<string, CapabilityInfo> All { get; } = s_all.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    public static RiskClass RiskOf(string id) =>
        All.TryGetValue(id, out CapabilityInfo? info) ? info.Risk : RiskClass.WriteExternal;

    public static RiskClass HighestRisk(IEnumerable<string> ids) =>
        ids.Select(RiskOf).DefaultIfEmpty(RiskClass.Read).Max();
}

/// <summary>
/// The placement rule. The highest risk class an agent uses, plus its runtime kind, sets the minimum isolation. A
/// manifest may ask for more. Nobody can configure less.
/// </summary>
public static class PlacementPolicy
{
    public static Placement Minimum(AgentManifest manifest)
    {
        Placement minimum = CapabilityCatalog.HighestRisk(manifest.Capabilities) >= RiskClass.WriteLocal
            ? Placement.Pool
            : Placement.InProcess;

        if (manifest.Runtime == RuntimeKind.Copilot)
        {
            minimum = Max(minimum, Placement.Pool);
        }

        if (manifest.Runtime == RuntimeKind.Remote)
        {
            minimum = Placement.Remote;
        }

        return manifest.Placement is { } requested ? Max(minimum, requested) : minimum;
    }

    /// <summary>True when a host that provides <paramref name="hostPlacement"/> may run an agent needing <paramref name="required"/>.</summary>
    public static bool Satisfies(Placement hostPlacement, Placement required) =>
        required switch
        {
            Placement.InProcess => true,
            Placement.Pool => hostPlacement == Placement.Pool,
            _ => hostPlacement == Placement.Remote,
        };

    private static Placement Max(Placement a, Placement b) => (Placement)Math.Max((int)a, (int)b);
}
