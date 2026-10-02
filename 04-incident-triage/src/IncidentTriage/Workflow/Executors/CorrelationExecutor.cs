using System.Text.Json;
using IncidentTriage.Agents;
using IncidentTriage.Ai;
using IncidentTriage.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace IncidentTriage.Workflow.Executors;

/// <summary>
/// Step 3 (agent: incident-correlator): group reports that describe the same incident.
/// </summary>
/// <remarks>
/// <para>Five pasted reports are often one outage seen from five angles (an alert, a log, a customer ticket...).
/// Triaging each separately would produce five duplicate tickets, so we cluster first.</para>
/// <para><b>Alternatives:</b> deterministic clustering (same service + overlapping time window) is cheaper
/// and fully predictable; embedding similarity between reports is a middle ground. An LLM is used here
/// because it copes with reports that name the same thing differently ("checkout", "payments page",
/// "POST /api/checkout"), and it explains its grouping, which the reviewer sees.</para>
/// </remarks>
public sealed class CorrelationExecutor(AIAgent agent, TriageJournal journal)
    : Executor<SignalsExtracted, CorrelatedIncidents>("correlate")
{
    public override async ValueTask<CorrelatedIncidents> HandleAsync(SignalsExtracted input, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            Group these incident signals into clusters, one cluster per distinct underlying incident.
            Every report id must appear in exactly one cluster.
            ```json
            {JsonSerializer.Serialize(input.Signals, JsonDefaults.Compact)}
            ```
            """;

        var response = await agent.RunAsync<CorrelationResult>(prompt, serializerOptions: JsonDefaults.Options, cancellationToken: cancellationToken);
        var clusters = Validate(response.Result.Clusters, input);

        journal.Record(JournalKind.AgentOutput, AgentNames.IncidentCorrelator, null, clusters);
        foreach (var c in clusters)
            await context.ReportAsync(Id, $"{c.ClusterId} ({c.Severity}) {c.Title}  <- {string.Join(", ", c.ReportIds)}", cancellationToken);

        return new CorrelatedIncidents(input, clusters);
    }

    /// <summary>
    /// Never trust a model's bookkeeping blindly: make sure every report landed in exactly one cluster.
    /// Cheap code checks after an LLM step catch most silent failures (dropped or duplicated items).
    /// </summary>
    private static IReadOnlyList<IncidentCluster> Validate(IReadOnlyList<IncidentCluster> clusters, SignalsExtracted input)
    {
        HashSet<string> seen = [];
        var cleaned = clusters
            .Select(c => c with { ReportIds = [.. c.ReportIds.Where(seen.Add)] })
            .Where(c => c.ReportIds.Count > 0)
            .ToList();

        // Orphans get their own cluster rather than disappearing.
        foreach (var orphan in input.Signals.Where(s => !seen.Contains(s.ReportId)))
            cleaned.Add(new IncidentCluster($"INC-{cleaned.Count + 1}", $"{orphan.Service}: {orphan.Summary}", orphan.Service, [orphan.ReportId], orphan.Severity, "Not grouped by the correlator; triaged on its own."));

        return cleaned;
    }
}
