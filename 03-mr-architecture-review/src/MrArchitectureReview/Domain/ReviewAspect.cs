namespace MrArchitectureReview.Domain;

/// <summary>
/// The review "lenses". Each one is handled by its own small, single-purpose agent.
/// </summary>
/// <remarks>
/// WHY one agent per aspect instead of one big "review this PR" agent?
/// <list type="bullet">
///   <item>Each prompt stays short and focused, which measurably improves recall on smaller models.</item>
///   <item>The three agents run concurrently (fan-out), so wall-clock time is the slowest agent, not the sum.</item>
///   <item>Humans accept/reject findings per aspect, so we can later tune <i>one</i> prompt
///         (e.g. "security is too noisy") without touching the others.</item>
/// </list>
/// Alternative: a single agent with a long checklist prompt. Cheaper (one call) and fine for tiny
/// diffs, but harder to evaluate and tune. Another alternative is the framework's built-in
/// <c>AgentWorkflowBuilder.BuildConcurrent(agents)</c>, which fans out chat messages to agents and
/// aggregates their chat output. We hand-build the graph instead because we want <i>typed</i>
/// messages (<see cref="AspectReview"/>) flowing between steps rather than raw chat transcripts.
/// </remarks>
public enum ReviewAspect
{
    Design,
    Security,
    Extensibility,
}

/// <summary>
/// C# 14 <b>extension members</b>. Before C# 14 we could only write extension <i>methods</i>
/// (<c>static string AgentName(this ReviewAspect a)</c>). The new <c>extension(T receiver) { ... }</c>
/// block also allows extension <i>properties</i>, so call sites read naturally:
/// <c>aspect.AgentName</c> instead of <c>aspect.AgentName()</c>.
/// Alternative: a switch expression at each call site, or attributes on the enum read via reflection
/// (more magic, slower, no compile-time checking).
/// Docs: https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-14#extension-members
/// </summary>
public static class ReviewAspectExtensions
{
    extension(ReviewAspect aspect)
    {
        /// <summary>Stable agent/executor id. Also the key for prompt and mock-response files.</summary>
        public string AgentName => aspect switch
        {
            ReviewAspect.Design => "design-reviewer",
            ReviewAspect.Security => "security-reviewer",
            ReviewAspect.Extensibility => "extensibility-reviewer",
            _ => throw new ArgumentOutOfRangeException(nameof(aspect), aspect, null),
        };

        /// <summary>Short id prefix used for finding ids, e.g. <c>SEC-1</c>.</summary>
        public string FindingPrefix => aspect switch
        {
            ReviewAspect.Design => "DES",
            ReviewAspect.Security => "SEC",
            ReviewAspect.Extensibility => "EXT",
            _ => throw new ArgumentOutOfRangeException(nameof(aspect), aspect, null),
        };
    }
}
