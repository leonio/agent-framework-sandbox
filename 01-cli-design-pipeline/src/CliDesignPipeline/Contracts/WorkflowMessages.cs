namespace CliDesignPipeline.Contracts;

// ---------------------------------------------------------------------------------------------
// Workflow messages: what flows along the edges of the graph.
//
// In Microsoft Agent Framework workflows, edges are *typed*: an executor declares the message
// types it handles, and the runtime only delivers a message to targets that can handle it. So
// these records are the protocol of the pipeline. Reading this file tells you every hand-off
// in the system without opening any executor.
//
// They are deliberately separate from AgentOutputs.cs: an agent's output is "what the model
// said", a workflow message is "what the next step needs". Sometimes they are the same shape,
// often the executor enriches it (attempt numbers, routing decisions, file lists read from disk).
// ---------------------------------------------------------------------------------------------

/// <summary>Start message: the raw product idea typed by the user.</summary>
public sealed record DesignBrief(string Idea, string WorkItemKey);

/// <summary>Sent to the "ask-stakeholder" request port. Surfaces in the host as a RequestInfoEvent.</summary>
public sealed record InterviewQuestion(
    int Number,
    int Budget,
    string Question,
    string Why,
    IReadOnlyList<string> SuggestedAnswers);

/// <summary>The human's reply to an <see cref="InterviewQuestion"/>.</summary>
public sealed record InterviewAnswer(string Text)
{
    /// <summary>Typing <c>/done</c> ends the interview early and forces a spec.</summary>
    public bool EndsInterview => Text.Trim() is "/done" or "/d";
}

/// <summary>Sent to the "approve-spec" request port: a human gate before any code is written.</summary>
public sealed record SpecApprovalRequest(RequirementsSpec Spec, string Markdown);

/// <summary>
/// The human's decision. <see cref="Reason"/> is mandatory for rejections and optional for
/// approvals; either way it is written to <c>decisions.jsonl</c> next to the AI output, so the
/// "why" behind every human call is kept with the work it shaped.
/// </summary>
public sealed record SpecApprovalDecision(bool Approved, string? Reason, string DecidedBy);

/// <summary>Interviewer -> Developer once the human has approved the spec.</summary>
public sealed record ApprovedSpec(RequirementsSpec Spec);

/// <summary>Developer -> Tester.</summary>
public sealed record ImplementationRound(int Round, DeveloperReport Report, IReadOnlyList<string> FilesOnDisk);

/// <summary>What the tester recommends happens next. Edges route on this.</summary>
public enum NextStep { Rework, Ship }

/// <summary>Tester -> Developer (Rework) or Tester -> Release (Ship).</summary>
public sealed record ReviewOutcome(int Round, TesterReport Report, NextStep Next);
