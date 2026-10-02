using System.ComponentModel;

namespace CliDesignPipeline.Contracts;

// ---------------------------------------------------------------------------------------------
// Structured outputs: what each agent must hand back.
//
// Every agent in this pipeline answers with JSON that deserialises into one of these records
// (via AIAgent.RunAsync<T>). That is the single most important design decision in the sample:
//
//   * The next step receives *data*, not prose it has to re-parse. The tester gets a list of
//     files, the release agent gets a verdict enum - no regex over model output.
//   * The JSON schema is generated from these types and sent to the model as the response
//     format, so the [Description] attributes below double as prompt text. Keep them precise.
//   * Records give us value equality and `with` expressions for free, and are trivially
//     serialisable for checkpointing / persistence (see sample 02, which stores them in a DB).
//
// Alternative: let agents talk in free text and hand the whole chat transcript to the next
// agent (AgentWorkflowBuilder.BuildSequential does exactly that). Great for brainstorming
// chains, poor for anything a program has to branch on.
// ---------------------------------------------------------------------------------------------

/// <summary>One turn of the requirements interview.</summary>
public sealed record InterviewTurn(
    [property: Description("True only when the interview is finished and `spec` is filled in.")]
    bool Done,
    [property: Description("The single next question for the stakeholder. Null when done.")]
    string? Question,
    [property: Description("One sentence telling the stakeholder why this question matters.")]
    string? Why,
    [property: Description("Two to four short suggested answers. The stakeholder may also type their own.")]
    IReadOnlyList<string>? SuggestedAnswers,
    [property: Description("The finished specification. Only when done is true.")]
    RequirementsSpec? Spec);

/// <summary>The contract between "what we want" and "what gets built".</summary>
public sealed record RequirementsSpec(
    [property: Description("Short product name, e.g. 'Team Task Tracker CLI'.")]
    string Title,
    [property: Description("Two or three sentences describing the app.")]
    string Summary,
    [property: Description("Who uses it.")]
    string PrimaryUser,
    [property: Description("'console' or 'minimal-api'.")]
    string AppKind,
    [property: Description("User stories in 'As a ..., I want ..., so that ...' form.")]
    IReadOnlyList<string> UserStories,
    [property: Description("Testable acceptance criteria, Given/When/Then preferred.")]
    IReadOnlyList<string> AcceptanceCriteria,
    [property: Description("Assumptions made where the stakeholder did not decide. Prefix none; the list says it.")]
    IReadOnlyList<string> Assumptions,
    [property: Description("Things explicitly not being built now.")]
    IReadOnlyList<string> OutOfScope);

/// <summary>What the developer agent reports after writing files.</summary>
public sealed record DeveloperReport(
    [property: Description("One paragraph describing what was implemented or fixed.")]
    string Summary,
    [property: Description("Relative paths of every file written in this round.")]
    IReadOnlyList<string> FilesWritten,
    [property: Description("Assumptions or shortcuts taken.")]
    IReadOnlyList<string> Assumptions);

public enum ReviewVerdict { Approved, ChangesRequested }

public enum Severity { Low, Medium, High }

public sealed record ReviewFinding(
    Severity Severity,
    [property: Description("Relative path of the file the finding is about.")]
    string File,
    [property: Description("What is wrong and what to do instead.")]
    string Message);

/// <summary>What the tester agent decides.</summary>
public sealed record TesterReport(
    ReviewVerdict Verdict,
    [property: Description("Two or three sentences summarising the review.")]
    string Summary,
    IReadOnlyList<ReviewFinding> Findings,
    [property: Description("Relative paths of the test files written.")]
    IReadOnlyList<string> TestsWritten);

/// <summary>What the release agent hands back once the paperwork is done.</summary>
public sealed record MergeRequestPlan(
    [property: Description("Branch name following the git-workflow skill.")]
    string Branch,
    string Title,
    [property: Description("Markdown body for the merge request.")]
    string Body,
    [property: Description("Exact shell commands, in order, to commit and push the work and open the MR.")]
    IReadOnlyList<string> Commands,
    [property: Description("URL of the created (or mocked) pull request, if any.")]
    string? PullRequestUrl,
    [property: Description("Anything that went wrong or needs a human.")]
    IReadOnlyList<string> Notes);
