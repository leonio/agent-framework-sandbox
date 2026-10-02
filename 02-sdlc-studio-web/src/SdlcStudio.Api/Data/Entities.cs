using SdlcStudio.Api.Domain;

namespace SdlcStudio.Api.Data;

// ---------------------------------------------------------------------------------------------
// EF Core entities.
//
// WHY plain mutable classes rather than records? EF Core change tracking works best with
// classes that have settable properties; records with value equality fight the identity map.
//
// WHY store AI output in the database at all (rather than only on disk)?
// The user asked that the context and status of every workflow is tracked. Keeping *what the AI
// produced* next to *what the human decided and why* (PhaseDecision, ReviewFinding.DecisionReason)
// gives you an audit trail and, later, a data set to evaluate or fine-tune prompts against.
// ---------------------------------------------------------------------------------------------

public enum TemplateKind
{
    /// <summary>System instructions for one agent ("who you are, how you behave").</summary>
    Instruction,
    /// <summary>A task prompt template with {{placeholders}} rendered per call.</summary>
    Prompt,
    /// <summary>A reusable capability appended to any agent that lists it (e.g. "conventional commits").</summary>
    Skill,
}

/// <summary>Admin-managed instructions, prompts and skills. Seeded from the Content folder.</summary>
public sealed class PromptTemplate
{
    public int Id { get; set; }

    /// <summary>
    /// C# 14 <c>field</c> keyword: we normalise keys without declaring a backing field by hand.
    /// ALTERNATIVE: a private <c>_key</c> field, or normalising in the endpoint (easy to forget).
    /// </summary>
    public required string Key
    {
        get;
        set => field = value.Trim().ToLowerInvariant();
    }

    public TemplateKind Kind { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public required string Content { get; set; }

    /// <summary>Comma separated skill keys (instructions only). Kept simple on purpose; a join table is the "proper" alternative.</summary>
    public string Skills { get; set; } = "";

    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkflowRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7(); // time-ordered GUIDs index nicely in SQL Server
    public required string Name { get; set; }
    public RunSource Source { get; set; }
    public WorkflowPhase Phase { get; set; }
    public RunStatus Status { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>Folder (under Studio:WorkspaceRoot) where specs, plans and generated code are written.</summary>
    public required string WorkspacePath { get; set; }

    /// <summary>Initial idea typed by the user (new idea runs).</summary>
    public string? Idea { get; set; }

    /// <summary>Path of the existing app to analyse (import runs).</summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// The interviewer's <c>AgentSession</c>, serialized with <c>AIAgent.SerializeSessionAsync</c>.
    /// This is how one agent conversation survives across many HTTP requests (and app restarts).
    /// </summary>
    public string? InterviewSessionJson { get; set; }

    public int ReviewRound { get; set; }

    /// <summary>The job the worker should run next, persisted so a restart can resume it.</summary>
    public string? PendingJob { get; set; }
    public string? PendingFeedback { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<RunMessage> Messages { get; set; } = [];
    public List<Artifact> Artifacts { get; set; } = [];
    public List<ReviewFinding> Findings { get; set; } = [];
    public List<Epic> Epics { get; set; } = [];
    public List<Story> Stories { get; set; } = [];
    public List<DevTask> Tasks { get; set; } = [];
    public List<Checkpoint> Checkpoints { get; set; } = [];
    public List<PhaseDecision> Decisions { get; set; } = [];
    public List<RunEvent> Events { get; set; } = [];
}

/// <summary>Interview transcript (requirements phase).</summary>
public sealed class RunMessage
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public required string Role { get; set; } // "user" | "assistant"
    public required string Content { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A document produced by a phase (spec, plan, test prompts, MR instructions...).</summary>
public sealed class Artifact
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public WorkflowPhase Phase { get; set; }
    public required string Kind { get; set; } // e.g. "requirements", "functional-spec", "technical-design"
    public required string Title { get; set; }
    public required string Content { get; set; }
    public string? RelativePath { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum FindingStatus { Pending, Accepted, Rejected }

/// <summary>One reviewer finding plus the human decision about it.</summary>
public sealed class ReviewFinding
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public int Round { get; set; }
    public required string Reviewer { get; set; }
    public required string Severity { get; set; }
    public required string Title { get; set; }
    public required string Detail { get; set; }
    public FindingStatus Status { get; set; }
    /// <summary>WHY the human accepted or rejected it. Fed back to the reviser agent.</summary>
    public string? DecisionReason { get; set; }
}

public sealed class Epic
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    public required string Key { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public string? ExternalId { get; set; } // Jira issue key
}

public enum WorkItemStatus { Todo, InProgress, Done }

public sealed class Story
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    public int EpicId { get; set; }
    public required string Key { get; set; }
    public required string Title { get; set; }
    public required string UserStory { get; set; }
    public string AcceptanceCriteria { get; set; } = ""; // newline separated
    public int Points { get; set; }
    public int Sprint { get; set; }
    public WorkItemStatus Status { get; set; }
    public string? ExternalId { get; set; } // Jira issue key
    public List<DevTask> Tasks { get; set; } = [];
}

public sealed class DevTask
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    public int StoryId { get; set; }
    public int Order { get; set; }
    public required string Title { get; set; }
    public required string Detail { get; set; }
    public string Files { get; set; } = "";
    public WorkItemStatus Status { get; set; }
    public string? CommitSha { get; set; }
}

public enum CheckpointKind { Commit, MergeRequest }

/// <summary>A small commit, or a merge-request "checkpoint" that a human approves.</summary>
public sealed class Checkpoint
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public int? StoryId { get; set; }
    public CheckpointKind Kind { get; set; }
    public required string Title { get; set; }
    public string Detail { get; set; } = "";
    public string? Branch { get; set; }
    public string? Sha { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Every approve / reject a human makes, with their reason.</summary>
public sealed class PhaseDecision
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public WorkflowPhase Phase { get; set; }
    public bool Approved { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Activity log: executor started/finished, tool calls, errors. Streamed to the UI.</summary>
public sealed class RunEvent
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public WorkflowPhase Phase { get; set; }
    public string Level { get; set; } = "info";
    public required string Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
