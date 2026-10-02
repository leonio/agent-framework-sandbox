using IncidentTriage.Persistence;
using IncidentTriage.Rag;
using IncidentTriage.Repo;
using Microsoft.Agents.AI.Workflows;

namespace IncidentTriage.Workflow.Executors;

/// <summary>
/// Step 1 (no LLM): fetch the code at the requested commit/tag and index it for RAG.
/// </summary>
/// <remarks>
/// <para><b>WHY is a non-AI step an executor?</b> Workflows are not only for agents. Putting deterministic work
/// (git, indexing, API calls) in executors gives it the same eventing, error handling and checkpointing as
/// the AI steps, and the graph shows the whole process, not just the model calls.</para>
/// <para><c>Executor&lt;TIn, TOut&gt;</c> is the simplest executor shape: one input type, one output type, and
/// the returned value is automatically sent along the outgoing edges.</para>
/// </remarks>
public sealed class IngestExecutor(RepositoryCloner cloner, KnowledgeIndex index, TriageJournal journal)
    : Executor<TriageRequest, TriageContext>("ingest")
{
    public override async ValueTask<TriageContext> HandleAsync(TriageRequest request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await context.ReportAsync(Id, $"Fetching {(string.IsNullOrWhiteSpace(request.Repo.Location) ? "the bundled demo repo" : request.Repo.Location)} @ {request.Repo.Ref ?? "(default branch)"}", cancellationToken);
        var snapshot = await cloner.ResolveAsync(request.Repo, cancellationToken);

        await context.ReportAsync(Id, $"Indexing code from {snapshot.LocalPath} (HEAD {snapshot.HeadCommit[..Math.Min(10, snapshot.HeadCommit.Length)]})", cancellationToken);
        var chunks = await index.IndexRepositoryAsync(snapshot, cancellationToken);

        journal.Record(JournalKind.Ingest, Id, snapshot.Location, new { snapshot.Ref, snapshot.HeadCommit, snapshot.IsDemo, CodeChunks = chunks, Reports = request.Reports.Count });
        await context.ReportAsync(Id, $"Indexed {chunks} code chunks. {request.Reports.Count} report(s) to triage.", cancellationToken);

        return new TriageContext(request, snapshot, chunks);
    }
}
