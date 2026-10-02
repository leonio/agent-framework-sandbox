namespace Roster.Agents;

/// <summary>How the agent's loop is run. See the design doc, section 5.</summary>
public enum RuntimeKind { Chat, Copilot, Remote }

/// <summary>Where an agent may run. Ordered from least to most isolated.</summary>
public enum Placement { InProcess = 0, Pool = 1, Remote = 2 }

/// <summary>How a typed result is obtained from the model. The runtime owns this, not the agent author.</summary>
public enum OutputStrategy { Native, Tool, Prompted }

/// <summary>What an agent asks for. Routing maps a tier to a concrete model for the chosen endpoint.</summary>
public enum ModelTier { Fast, Balanced, Reasoning }

/// <summary>Whether skills are appended to the instructions (inline) or loaded when the model asks (on demand).</summary>
public enum SkillMode { Inline, OnDemand }

/// <summary>The parsed front matter of an <c>AGENT.md</c>.</summary>
public sealed record AgentManifest
{
    public required string Name { get; init; }

    public string Description { get; init; } = "";

    /// <summary>The family the agent belongs to (reviewer, analyst, writer...). Archetypes share contracts, never agents.</summary>
    public string Archetype { get; init; } = "";

    /// <summary>A human-readable label. The content hash on <see cref="AgentDefinition"/> is what scorecards key on.</summary>
    public string Version { get; init; } = "1";

    public ModelTier Tier { get; init; } = ModelTier.Balanced;

    public RuntimeKind Runtime { get; init; } = RuntimeKind.Chat;

    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>Capability ids (see <see cref="CapabilityCatalog"/>). The host binds them to concrete tools.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public IReadOnlyList<string> Knowledge { get; init; } = [];

    /// <summary>Name of the input contract, or null for a conversational agent.</summary>
    public string? Input { get; init; }

    /// <summary>Name of the output contract, or null for a conversational agent.</summary>
    public string? Output { get; init; }

    public OutputStrategy OutputStrategy { get; init; } = OutputStrategy.Native;

    public SkillMode SkillMode { get; init; } = SkillMode.Inline;

    /// <summary>A request for more isolation than the policy would require. Never less.</summary>
    public Placement? Placement { get; init; }
}

public sealed record ResolvedSkill(string Name, string Description, string Body);

/// <summary>An agent ready to run: manifest, composed instructions, skills, contract types and a content hash.</summary>
public sealed record AgentDefinition(
    AgentManifest Manifest,
    string Instructions,
    IReadOnlyList<ResolvedSkill> Skills,
    Type? InputType,
    Type? OutputType,
    string Hash)
{
    public string Name => Manifest.Name;

    /// <summary>The first 12 hex characters of the hash, for display.</summary>
    public string ShortHash => Hash.Length > 12 ? Hash[..12] : Hash;
}
