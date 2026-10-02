using IncidentTriage.Agents;
using IncidentTriage.Ai;
using IncidentTriage.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace IncidentTriage.Workflow.Executors;

/// <summary>
/// Step 2 (agent: signal-extractor): turn each free-text report into structured <see cref="IncidentSignals"/>.
/// </summary>
/// <remarks>
/// <para><b>Parallelism inside an executor vs. in the graph.</b> The number of reports is only known at run
/// time, while workflow edges are fixed when the graph is built. So we fan out here with
/// <c>Task.WhenAll</c>: one agent call per report, concurrently. Graph-level fan-out
/// (<c>AddFanOutEdge</c>) is used later where the branches are <i>different</i> agents (Jira and
/// Confluence). Alternative for dynamic fan-out: a sub-workflow per report via
/// <c>workflow.BindAsExecutor(...)</c>, which buys per-report checkpointing at the cost of more ceremony.</para>
/// <para><b>Structured output.</b> <c>RunAsync&lt;IncidentSignals&gt;</c> sends a JSON schema derived from the
/// record and deserialises the reply, so the next step gets typed data instead of prose to re-parse.</para>
/// </remarks>
public sealed class SignalExtractionExecutor(AIAgent agent, TriageJournal journal)
    : Executor<TriageContext, SignalsExtracted>("extract-signals")
{
    public override async ValueTask<SignalsExtracted> HandleAsync(TriageContext input, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await context.ReportAsync(Id, $"Extracting signals from {input.Request.Reports.Count} report(s) in parallel");

        var signals = await Task.WhenAll(input.Request.Reports.Select(async report =>
        {
            // Untrusted input is fenced with explicit delimiters, and the prompt file tells the model that
            // text inside them is data, never instructions. It does not make prompt injection impossible,
            // but it makes it much harder, and the downstream human gates catch the rest.
            var prompt = $"""
                Extract incident signals from the report below.
                Report id: {report.Id}
                <<<
                {report.Text}
                >>>
                """;

            var response = await agent.RunAsync<IncidentSignals>(prompt, serializerOptions: JsonDefaults.Options, cancellationToken: cancellationToken);
            var result = response.Result with { ReportId = report.Id }; // trust our id, not the model's copy of it

            journal.Record(JournalKind.AgentOutput, AgentNames.SignalExtractor, report.Id, result);
            await context.ReportAsync(Id, $"{report.Id}: {result.Service} {result.Severity} [{string.Join(", ", result.ErrorSignatures.Take(2))}]", cancellationToken);
            return result;
        }));

        return new SignalsExtracted(input, signals);
    }
}
