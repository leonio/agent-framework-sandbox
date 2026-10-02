using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MrArchitectureReview.Domain;

// -------------------------------------------------------------------------------------------------
// Domain model.
//
// Everything here is an immutable `record`. WHY records?
//  * Workflow messages are passed between executors that may run concurrently. Immutable messages
//    mean no executor can mutate data another one is still reading.
//  * The framework serialises messages when it checkpoints a workflow (see the checkpointing note in
//    ReviewWorkflowFactory). Records with simple properties serialise with System.Text.Json and no
//    extra configuration.
//  * Value equality makes the unit tests trivial.
// Alternative: mutable classes + defensive copies (old-school, verbose) or protobuf/DTO classes if
// the workflow ever runs out-of-process.
// -------------------------------------------------------------------------------------------------

/// <summary>What the user asked for: which pull request to review.</summary>
/// <param name="Source">Either a GitHub PR URL or the literal <c>sample</c> for the bundled offline fixture.</param>
public sealed record ReviewRequest(string Source);

/// <summary>One file touched by the PR.</summary>
public sealed record ChangedFile(string Path, string Status, int Additions, int Deletions);

/// <summary>
/// A point-in-time snapshot of the PR: metadata, the unified diff and a local checkout of the head
/// commit so tools can read surrounding code that is not in the diff.
/// </summary>
public sealed record PullRequestSnapshot(
    string Owner,
    string Repository,
    int Number,
    string Title,
    string Description,
    string Author,
    string BaseRef,
    string HeadRef,
    IReadOnlyList<ChangedFile> Files,
    string Diff,
    string LocalCheckoutPath)
{
    /// <summary>Key used to group review history per repository (for the feedback loop).</summary>
    public string RepositoryKey => $"{Owner}/{Repository}";
}

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity { Info, Low, Medium, High }

/// <summary>
/// A single observation as the <b>model</b> produces it (the structured-output contract).
/// </summary>
/// <remarks>
/// The <see cref="DescriptionAttribute"/>s are not just documentation: when an agent is asked for
/// structured output (<c>agent.RunAsync&lt;AspectFindings&gt;(...)</c>), Microsoft.Extensions.AI builds a
/// JSON schema from this type and the descriptions become part of that schema. They are effectively
/// a second, very precise prompt. Keep them short and imperative.
/// <para>
/// WHY a separate "draft" type rather than asking the model for <see cref="Finding"/> directly?
/// Ids and the aspect are facts the <i>workflow</i> owns. If they were in the schema the model would
/// invent them (and sometimes collide). Splitting "what the model may say" from "what the system
/// records" is a cheap guard against hallucinated identifiers.
/// </para>
/// </remarks>
public sealed record FindingDraft(
    [property: Description("Short headline, max ~10 words.")] string Title,
    [property: Description("What is wrong and why it matters, 1-3 sentences. Refer to concrete code.")] string Detail,
    [property: Description("Concrete, minimal change that would address it.")] string Recommendation,
    [property: Description("Info, Low, Medium or High.")] Severity Severity,
    [property: Description("Repository-relative file path, if the finding is tied to one file, else null.")] string? FilePath,
    [property: Description("0..1 - how sure you are this is a real problem, not a style preference.")] double Confidence);

/// <summary>
/// The structured-output contract for every reviewer agent. Kept deliberately tiny: the less the
/// model has to produce, the more reliable the JSON is.
/// </summary>
public sealed record AspectFindings(
    [property: Description("One or two sentence overall assessment for this aspect.")] string Summary,
    [property: Description("Zero to five findings, most important first. Empty if nothing material.")] IReadOnlyList<FindingDraft> Findings);

/// <summary>A finding as the system records it: the model's draft plus workflow-owned identity.</summary>
public sealed record Finding(
    string Id,
    ReviewAspect Aspect,
    string Title,
    string Detail,
    string Recommendation,
    Severity Severity,
    string? FilePath,
    double Confidence)
{
    public static Finding From(FindingDraft draft, ReviewAspect aspect, int ordinal) => new(
        Id: $"{aspect.FindingPrefix}-{ordinal}",
        Aspect: aspect,
        Title: draft.Title,
        Detail: draft.Detail,
        Recommendation: draft.Recommendation,
        Severity: draft.Severity,
        FilePath: draft.FilePath,
        // Models occasionally return 0-100 instead of 0-1. Clamp rather than trust.
        Confidence: Math.Clamp(draft.Confidence > 1 ? draft.Confidence / 100 : draft.Confidence, 0, 1));
}

/// <summary>Output of one reviewer executor: findings plus provenance for the audit trail.</summary>
public sealed record AspectReview(
    ReviewAspect Aspect,
    string Summary,
    IReadOnlyList<Finding> Findings,
    string PromptSha,
    TimeSpan Duration);

/// <summary>The three aspect reviews merged, de-duplicated and ordered for the human.</summary>
public sealed record ConsolidatedReview(
    string RunId,
    IReadOnlyList<AspectReview> Aspects,
    IReadOnlyList<Finding> Findings);

[JsonConverter(typeof(JsonStringEnumConverter<Verdict>))]
public enum Verdict { Accepted, Rejected, Deferred }

/// <summary>
/// Sent OUT of the workflow to a human through a <c>RequestPort</c>. One request per finding.
/// </summary>
public sealed record TriageRequest(Finding Finding, int Position, int Total);

/// <summary>
/// The human's answer, sent back INTO the workflow. The <see cref="Reason"/> is the valuable part:
/// it is what lets future runs learn that "we accept raw SQL in migrations because ..." and stop
/// raising the same finding (see <c>Memory/ReviewHistoryContextProvider</c>).
/// </summary>
public sealed record TriageDecision(string FindingId, Verdict Verdict, string Reason, string DecidedBy, DateTimeOffset DecidedAt);

/// <summary>A finding paired with what the human decided about it.</summary>
public sealed record TriagedFinding(Finding Finding, TriageDecision Decision);

/// <summary>
/// The full, persisted record of one review run: AI output and human decisions side by side.
/// This is the artefact you would keep for audit, prompt tuning and evaluation.
/// </summary>
public sealed record ReviewRecord(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string RepositoryKey,
    int PullRequestNumber,
    string PullRequestTitle,
    string HeadRef,
    string ModelProvider,
    string Model,
    IReadOnlyList<AspectReview> Aspects,
    IReadOnlyList<TriagedFinding> Findings)
{
    public int AcceptedCount => Findings.Count(f => f.Decision.Verdict is Verdict.Accepted);
    public int RejectedCount => Findings.Count(f => f.Decision.Verdict is Verdict.Rejected);
}

/// <summary>What the publisher agent did with the accepted findings (tickets, comments, wiki).</summary>
public sealed record PublishOutcome(ReviewRecord Record, string RecordPath, string PublisherSummary);
