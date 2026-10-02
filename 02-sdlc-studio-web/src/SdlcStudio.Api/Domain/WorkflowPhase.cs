namespace SdlcStudio.Api.Domain;

/// <summary>
/// The macro phases of a studio run. Each phase is executed by its own small
/// Agent Framework <c>Workflow</c> (see the Workflows folder), and the human gates
/// between phases are persisted in the database.
/// </summary>
/// <remarks>
/// WHY one workflow per phase instead of one giant workflow for the whole SDLC?
/// <list type="bullet">
/// <item>The gaps between phases are measured in hours or days (a human reviews a spec,
/// a PO approves a plan). Keeping an in-memory workflow suspended that long is fragile.</item>
/// <item>Each phase can be re-run on its own ("reject the plan, try again with this feedback").</item>
/// <item>Small graphs are much easier to read, test and reason about.</item>
/// </list>
/// ALTERNATIVE: model the whole SDLC as one workflow and pause at human gates with a
/// <c>RequestPort</c> (emits a <c>RequestInfoEvent</c>), persisting progress with
/// <c>CheckpointManager</c> + a custom <c>JsonCheckpointStore</c> backed by this database.
/// That gives you "resume exactly where you left off" semantics, at the cost of having to
/// version checkpoint payloads whenever executors change. For long-running, durable
/// orchestration in production also look at Durable Task / Azure Durable Functions hosting.
/// </remarks>
public enum WorkflowPhase
{
    Requirements,
    Import,
    Design,
    Review,
    AgilePlan,
    DevPlan,
    Develop,
    Testing,
    Done,
}

public enum RunStatus
{
    /// <summary>An agent workflow is queued or running for the current phase.</summary>
    Running,
    /// <summary>The interviewer asked a question and is waiting for the user's answer.</summary>
    AwaitingInput,
    /// <summary>The phase produced output that a human must approve or reject.</summary>
    AwaitingApproval,
    Failed,
    Completed,
}

public enum RunSource
{
    /// <summary>Requirements are gathered by the "grill me" interviewer agent.</summary>
    NewIdea,
    /// <summary>Specs are reverse-engineered from an existing code base.</summary>
    ExistingApp,
}

/// <summary>
/// C# 14 extension members: an <c>extension</c> block lets us add *properties* (not just
/// methods) to a type we cannot change - here, an enum.
/// </summary>
/// <remarks>
/// WHY: phase metadata (display name, ordering) lives next to the enum instead of being
/// scattered through switch statements in endpoints and UI DTOs.
/// ALTERNATIVE (pre C# 14): static helper methods <c>PhaseInfo.DisplayName(phase)</c>, or
/// attributes on the enum members read via reflection (slower and stringly typed).
/// </remarks>
public static class WorkflowPhaseExtensions
{
    extension(WorkflowPhase phase)
    {
        public string DisplayName => phase switch
        {
            WorkflowPhase.Requirements => "Requirements interview",
            WorkflowPhase.Import => "Import existing app",
            WorkflowPhase.Design => "Design specs",
            WorkflowPhase.Review => "Review specs",
            WorkflowPhase.AgilePlan => "Agile plan",
            WorkflowPhase.DevPlan => "Dev plan",
            WorkflowPhase.Develop => "Develop",
            WorkflowPhase.Testing => "Testing",
            WorkflowPhase.Done => "Done",
            _ => phase.ToString(),
        };

        /// <summary>True for phases that end with a human approve / reject gate.</summary>
        public bool HasApprovalGate => phase is WorkflowPhase.Requirements or WorkflowPhase.Review
            or WorkflowPhase.AgilePlan or WorkflowPhase.DevPlan or WorkflowPhase.Develop or WorkflowPhase.Testing;
    }

    extension(RunSource source)
    {
        /// <summary>The ordered list of phases a run goes through, shown as the UI stepper.</summary>
        public IReadOnlyList<WorkflowPhase> Pipeline => source switch
        {
            RunSource.ExistingApp =>
                [WorkflowPhase.Import, WorkflowPhase.Review, WorkflowPhase.AgilePlan, WorkflowPhase.DevPlan,
                 WorkflowPhase.Develop, WorkflowPhase.Testing, WorkflowPhase.Done],
            _ =>
                [WorkflowPhase.Requirements, WorkflowPhase.Design, WorkflowPhase.Review, WorkflowPhase.AgilePlan,
                 WorkflowPhase.DevPlan, WorkflowPhase.Develop, WorkflowPhase.Testing, WorkflowPhase.Done],
        };
    }
}
