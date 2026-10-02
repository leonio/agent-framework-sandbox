using System.ComponentModel;
using System.Text.Json.Serialization;
using IncidentTriage.Repo;

namespace IncidentTriage.Workflow;

// =================================================================================================
// Messages that flow along the workflow's edges, plus the structured-output contracts the agents fill.
//
// WHY immutable records?
//   Agent Framework workflows run in "supersteps" (a Pregel-style model): every message produced in
//   step N is delivered in step N+1, possibly to several executors in parallel (fan-out). Immutable
//   records make that safe by construction: no executor can mutate a message another one is reading.
//   They also serialise cleanly, which matters if you turn on checkpointing (CheckpointManager) so a
//   run can be resumed after a crash or after waiting days for a human reviewer.
//   Docs: https://learn.microsoft.com/agent-framework/user-guide/workflows/core-concepts/edges
//
// WHY [Description] attributes on the agent contracts?
//   AIAgent.RunAsync<T>() turns T into a JSON schema and sends it as the response format. The
//   descriptions end up in that schema, so they are prompt text the model actually reads. Keep them
//   short and specific; they are cheaper than repeating the same guidance in every prompt file.
//
// ALTERNATIVE: one big mutable "TriageState" object stored with IWorkflowContext.QueueStateUpdateAsync.
//   That is closer to LangGraph's shared-state style. We prefer explicit typed messages because the
//   graph then documents itself (each edge says exactly what it carries) and the type system checks
//   that an executor can only receive what it declared it handles.
// =================================================================================================

// ---------- Input -------------------------------------------------------------------------------

/// <summary>Where the code lives. <paramref name="Ref"/> may be a branch, tag or commit id.</summary>
public sealed record RepoSource(string Location, string? Ref);

/// <summary>One pasted incident report (alert text, log excerpt, customer ticket, chat transcript...).</summary>
public sealed record IncidentReport(string Id, string Text);

/// <summary>The workflow's input message. The workflow's start executor declares it handles this type.</summary>
public sealed record TriageRequest(string RunId, RepoSource Repo, IReadOnlyList<IncidentReport> Reports);

// ---------- Ingest -----------------------------------------------------------------------------

/// <summary>Request + the checked-out code it refers to. Carried forward so later steps can cite commits.</summary>
public sealed record TriageContext(TriageRequest Request, RepoSnapshot Snapshot, int IndexedCodeChunks);

// ---------- Signal extraction (agent: signal-extractor) ------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity { Sev1, Sev2, Sev3, Sev4 }

[Description("Facts pulled out of a single incident report. Never guess: leave a field empty when the report does not say.")]
public sealed record IncidentSignals(
    [property: Description("The report id exactly as given in the input.")] string ReportId,
    [property: Description("The service or component most clearly affected, e.g. 'checkout-api'.")] string Service,
    [property: Description("One sentence describing what users or systems experienced.")] string Summary,
    [property: Description("Exception types, error codes, HTTP statuses or log message fragments, verbatim.")] IReadOnlyList<string> ErrorSignatures,
    [property: Description("Observable symptoms such as latency, error rate, timeouts.")] IReadOnlyList<string> Symptoms,
    [property: Description("Earliest timestamp mentioned, ISO-8601, or empty.")] string FirstSeen,
    [property: Description("Severity using the severity-rubric skill.")] Severity Severity);

public sealed record SignalsExtracted(TriageContext Context, IReadOnlyList<IncidentSignals> Signals);

// ---------- Correlation (agent: incident-correlator) ---------------------------------------------

[Description("A group of reports that describe the same underlying incident.")]
public sealed record IncidentCluster(
    [property: Description("Short stable id such as 'INC-1'.")] string ClusterId,
    [property: Description("A headline a human would put on the incident channel.")] string Title,
    [property: Description("Primary affected service.")] string Service,
    [property: Description("Ids of every report in this cluster.")] IReadOnlyList<string> ReportIds,
    [property: Description("Highest severity among the grouped reports.")] Severity Severity,
    [property: Description("Why these reports belong together (shared signatures, time window, service).")] string Rationale);

public sealed record CorrelationResult(IReadOnlyList<IncidentCluster> Clusters);

public sealed record CorrelatedIncidents(SignalsExtracted Extracted, IReadOnlyList<IncidentCluster> Clusters);

// ---------- Root cause (agent: root-cause-analyst) + human review -------------------------------

[Description("A piece of evidence supporting the hypothesis, citing where it came from.")]
public sealed record Evidence(
    [property: Description("Where it came from: a report id, 'runbook:<file>', 'postmortem:<file>', 'code:<path>:<line>' or 'commit:<sha>'.")] string Source,
    [property: Description("What the source shows, in one or two sentences.")] string Detail);

[Description("The most likely root cause for one incident cluster.")]
public sealed record RootCauseHypothesis(
    [property: Description("One or two sentences: what broke and why.")] string Summary,
    [property: Description("Category such as 'deploy regression', 'capacity', 'dependency outage', 'config', 'data'.")] string Category,
    [property: Description("Component, file or commit most likely responsible.")] string SuspectedComponent,
    [property: Description("Confidence between 0 and 1. Below 0.5 means 'needs a human to dig'.")] double Confidence,
    [property: Description("Evidence with sources. At least two items.")] IReadOnlyList<Evidence> Evidence,
    [property: Description("Immediate mitigation steps, taken from the runbook when one applies.")] IReadOnlyList<string> Mitigations,
    [property: Description("Runbook files that apply, e.g. 'db-connection-pool-exhaustion.md'.")] IReadOnlyList<string> RunbookReferences);

[JsonConverter(typeof(JsonStringEnumConverter<ReviewVerdict>))]
public enum ReviewVerdict { Accept, Reject, Skip }

/// <summary>Sent OUT of the workflow through a RequestPort: "human, please review this".</summary>
public sealed record RootCauseReviewRequest(IncidentCluster Cluster, RootCauseHypothesis Hypothesis, int Round, int MaxRounds);

/// <summary>Sent back INTO the workflow by the host. The reason is stored next to the AI output.</summary>
public sealed record ReviewDecision(ReviewVerdict Verdict, string? Reason, string Reviewer);

/// <summary>A cluster whose root cause a human accepted (or explicitly skipped).</summary>
public sealed record ReviewedIncident(IncidentCluster Cluster, RootCauseHypothesis Hypothesis, ReviewVerdict FinalVerdict, IReadOnlyList<ReviewRound> History);

public sealed record ReviewRound(int Round, RootCauseHypothesis Hypothesis, ReviewDecision Decision);

/// <summary>Everything downstream writers need. This is the message that fans out.</summary>
public sealed record TriageAnalysis(CorrelatedIncidents Correlated, IReadOnlyList<ReviewedIncident> Incidents)
{
    public TriageContext Context => Correlated.Extracted.Context;
}

// ---------- Writers (agents: jira-drafter, postmortem-writer) ------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter<JiraPriority>))]
public enum JiraPriority { Highest, High, Medium, Low }

[Description("A Jira issue ready to be created. Plain text, no Jira wiki markup.")]
public sealed record JiraIssueDraft(
    [property: Description("Cluster id this ticket is for.")] string ClusterId,
    [property: Description("Ticket summary, max 120 characters, starts with the service name.")] string Summary,
    [property: Description("Description: impact, root cause, evidence, mitigation, follow-ups.")] string Description,
    [property: Description("Bug, Incident or Task.")] string IssueType,
    [property: Description("Priority mapped from severity: Sev1=Highest, Sev2=High, Sev3=Medium, Sev4=Low.")] JiraPriority Priority,
    [property: Description("Lower-case labels, e.g. 'incident', service name, category.")] IReadOnlyList<string> Labels,
    [property: Description("Keys of existing issues that look related, from the search tool. Empty if none.")] IReadOnlyList<string> RelatedIssueKeys);

public sealed record JiraDraftSet(IReadOnlyList<JiraIssueDraft> Issues);

/// <summary>Output of the Jira branch of the fan-out.</summary>
public sealed record JiraDrafts(TriageAnalysis Analysis, IReadOnlyList<JiraIssueDraft> Issues);

public sealed record PostmortemDraft(string ClusterId, string Title, string Markdown);

/// <summary>Output of the Confluence branch of the fan-out.</summary>
public sealed record PostmortemDrafts(TriageAnalysis Analysis, IReadOnlyList<PostmortemDraft> Pages);

// ---------- Publish (second human gate) ----------------------------------------------------------

public sealed record PublishApprovalRequest(IReadOnlyList<JiraIssueDraft> Issues, IReadOnlyList<PostmortemDraft> Pages, string OutputFolder);

public sealed record PublishDecision(bool Approved, string? Reason, string Reviewer);

/// <summary>The workflow's final output, yielded with IWorkflowContext.YieldOutputAsync.</summary>
public sealed record TriageReport(
    string RunId,
    string OutputFolder,
    IReadOnlyList<ReviewedIncident> Incidents,
    IReadOnlyList<string> CreatedIssueKeys,
    IReadOnlyList<string> CreatedPageUrls,
    bool Published);
