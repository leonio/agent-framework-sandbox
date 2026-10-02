namespace Roster.Platform.Data;

/// <summary>
/// The ledger: one row per agent call, written by whoever made the call (see <see cref="LedgerRecorder"/>). It records
/// exactly which agent version ran on which endpoint and model, what it was given, what it returned, which tools it
/// called, its reasoning when the endpoint returned any, the tokens, the time taken and the trace id. Findings, the
/// retro and scorecards all point back here.
/// </summary>
public sealed class Invocation
{
    public Guid Id { get; set; }

    public Guid AssignmentId { get; set; }

    /// <summary>Whose behalf the call was made on (the assignment's owner).</summary>
    public required string UserId { get; set; }

    public required string PhaseKey { get; set; }

    /// <summary>Which step within the phase: usually the agent's name, or <c>retro</c> for facilitator turns.</summary>
    public required string StepKey { get; set; }

    /// <summary>The phase attempt this call belongs to. A retried phase adds rows; it never overwrites them.</summary>
    public int Attempt { get; set; }

    public required string AgentName { get; set; }

    /// <summary>The content hash that identifies this exact agent behaviour (see <see cref="AgentVersion"/>).</summary>
    public required string AgentHash { get; set; }

    public Guid? EndpointId { get; set; }

    /// <summary>Copied, so the row stays readable if the endpoint is renamed or deleted.</summary>
    public required string EndpointName { get; set; }

    public required string Model { get; set; }

    /// <summary><c>chat</c> in slice 1.</summary>
    public required string RuntimeKind { get; set; }

    /// <summary><c>native</c>, <c>prompted</c> or, for conversation turns, <c>text</c>.</summary>
    public required string OutputStrategy { get; set; }

    /// <summary>The rendered input exactly as the model saw it, untrusted fences included.</summary>
    public required string InputText { get; set; }

    /// <summary>
    /// <c>succeeded</c>, <c>repaired</c>, <c>invalid-output</c>, <c>failed</c> or <c>cancelled</c>. Null while running.
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>The validated output (typed agents) as JSON.</summary>
    public string? OutputJson { get; set; }

    /// <summary>The model's last reply as text.</summary>
    public string? RawText { get; set; }

    public string? Error { get; set; }

    /// <summary><c>[{ "name", "arguments", "result" }]</c> in call order.</summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>
    /// The model's reasoning, when the endpoint returned it. May contain sensitive content: only the assignment's owner
    /// and the retro facilitator read it.
    /// </summary>
    public string? Reasoning { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public long? DurationMs { get; set; }

    public string? TraceId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// An agent version: one row per distinct content hash an agent has run with. Slice 1 records the library baselines
/// the first time each hash runs; slice 2 adds versions edited in the UI. Scorecards group by this.
/// </summary>
public sealed class AgentVersion
{
    public Guid Id { get; set; }

    public required string AgentName { get; set; }

    public required string Hash { get; set; }

    /// <summary>The human-readable label from the manifest. Informational; the hash is the identity.</summary>
    public required string VersionLabel { get; set; }

    public string Archetype { get; set; } = "";

    /// <summary>The instructions at that version, so a scorecard can show what the agent was told.</summary>
    public required string Instructions { get; set; }

    /// <summary>The skills it had, in order, as a JSON array of names.</summary>
    public string SkillsJson { get; set; } = "[]";

    /// <summary><c>library</c> for the shipped baseline; <c>edited</c> for UI edits (slice 2).</summary>
    public string Source { get; set; } = "library";

    public DateTimeOffset FirstSeenAt { get; set; }
}
