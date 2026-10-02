namespace Roster.Platform.Data;

public enum AssignmentState
{
    /// <summary>Created; its first phase is waiting in the queue.</summary>
    Queued,

    /// <summary>A phase is running on a runner.</summary>
    Running,

    /// <summary>The agents are done and the person is deciding on the findings.</summary>
    AwaitingTriage,

    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// A unit of work a person gives to a team of agents: one run of a scenario (slice 1: <c>pr-review</c>). It moves
/// through its <see cref="Phase"/>s, one job at a time, and ends with the person's decisions and, ideally, a retro.
/// </summary>
public sealed class Assignment
{
    public Guid Id { get; set; }

    public required string OwnerId { get; set; }

    /// <summary>The scenario key, such as <c>pr-review</c>. The scenario decides the phases.</summary>
    public required string Scenario { get; set; }

    public required string Title { get; set; }

    /// <summary>What to work on: <c>fixture:sample-pr</c>, or a pull request URL such as <c>https://github.com/o/r/pull/1</c>.</summary>
    public required string Source { get; set; }

    public AssignmentState State { get; set; }

    /// <summary>The endpoint the person picked for the whole assignment (routing step 3). Null falls through to defaults.</summary>
    public Guid? EndpointId { get; set; }

    /// <summary>A model on that endpoint, overriding the tier mapping. Usually null.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// The advanced panel's choices as JSON, read by <see cref="ModelRouter"/>:
    /// <c>{"phases": {"review": {"endpointId": "…", "model": "…"}}, "steps": {"review/security-reviewer": {…}}}</c>.
    /// </summary>
    public string OverridesJson { get; set; } = "{}";

    /// <summary>Set by the cancel button. Runners check it between steps and stop.</summary>
    public bool CancelRequested { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public List<Phase> Phases { get; set; } = [];
}

public enum PhaseState { Pending, Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>
/// A stage of a scenario (fetch, review, triage), persisted so it can be retried on its own and so the UI can show a
/// stepper. A phase's output is the next phase's input: the fetch phase stores the pull request snapshot, for example.
/// </summary>
public sealed class Phase
{
    public Guid Id { get; set; }

    public Guid AssignmentId { get; set; }

    /// <summary>The scenario's name for it: <c>fetch</c>, <c>review</c>, <c>triage</c>.</summary>
    public required string Key { get; set; }

    /// <summary>Position in the scenario, from 0.</summary>
    public int Order { get; set; }

    public PhaseState State { get; set; }

    /// <summary>How many times it has started. Ledger rows are keyed by phase and attempt, so a retry never overwrites history.</summary>
    public int Attempt { get; set; }

    /// <summary>The phase's result as JSON, for the phases after it.</summary>
    public string? OutputJson { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public enum FindingDecision { Pending, Accepted, Rejected }

/// <summary>
/// One finding from a reviewer agent, plus what the person decided about it and why. The reason is the most valuable
/// field in the system: it is what the retro asks about and what scorecards learn from.
/// </summary>
public sealed class Finding
{
    public Guid Id { get; set; }

    public Guid AssignmentId { get; set; }

    /// <summary>The ledger row of the agent call that produced it.</summary>
    public Guid InvocationId { get; set; }

    public required string AgentName { get; set; }

    /// <summary>The agent's content hash at the time, so scorecards credit the exact version.</summary>
    public required string AgentHash { get; set; }

    /// <summary>The agent's own order (most important first).</summary>
    public int Rank { get; set; }

    public required string Title { get; set; }

    public required string Detail { get; set; }

    public required string Recommendation { get; set; }

    /// <summary><c>info</c>, <c>low</c>, <c>medium</c> or <c>high</c>, as the contract spells it.</summary>
    public required string Severity { get; set; }

    public string? FilePath { get; set; }

    public double Confidence { get; set; }

    public FindingDecision Decision { get; set; }

    /// <summary>Why the person accepted or rejected it, in their words. Required for a rejection.</summary>
    public string? DecisionReason { get; set; }

    public string? DecidedById { get; set; }

    /// <summary>Name and title copied at decision time (see <see cref="AppUser.Title"/>).</summary>
    public string? DecidedByName { get; set; }

    public string? DecidedByTitle { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
