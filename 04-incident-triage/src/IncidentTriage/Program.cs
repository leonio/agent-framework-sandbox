// =================================================================================================
// Incident triage: a multi-agent workflow built with Microsoft Agent Framework for .NET.
//
//   repo (+ optional commit/tag) + pasted reports
//     -> clone & index code (RAG)            [executor, no LLM]
//     -> extract signals per report          [agent, parallel, structured output]
//     -> correlate into incidents            [agent]
//     -> root cause per incident             [agent + RAG (runbooks pushed, code pulled) + tools + mocked MCP]
//          <-> human accepts / rejects WITH A REASON, reason fed back to the agent
//     -> Jira ticket  || postmortem          [two agents in parallel: fan-out / fan-in]
//     -> human approves publish -> Jira + Confluence API calls (dry-run)
//
// Everything the AI produced and every human decision (with its reason) lands in output/<run>/journal.json.
//
// Top-level statements: the C# 9+ default for console apps. Program structure lives in the classes;
// this file is only composition (build the objects, wire them, run, render events).
// =================================================================================================

using IncidentTriage;
using IncidentTriage.Agents;
using IncidentTriage.Ai;
using IncidentTriage.Configuration;
using IncidentTriage.Persistence;
using IncidentTriage.Prompting;
using IncidentTriage.Rag;
using IncidentTriage.Repo;
using IncidentTriage.Tools;
using IncidentTriage.Workflow;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Configuration;

var cli = CliArgs.Parse(args);
var prompts = PromptLibrary.Discover();

// ---- Configuration -----------------------------------------------------------------------------
// Later sources win: appsettings.json < user-secrets < environment (Ai__ApiKey) < command line (--Ai:Provider).
// Secrets belong in user-secrets locally (`dotnet user-secrets set Ai:ApiKey ...`) and Key Vault in Azure.
var config = new ConfigurationBuilder()
    .SetBasePath(prompts.ContentRoot)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(cli.ConfigArgs)
    .Build();

var aiOptions = config.GetSection("Ai").Get<AiOptions>() ?? new();
var triageOptions = config.GetSection("Triage").Get<TriageOptions>() ?? new();
var jiraOptions = config.GetSection("Jira").Get<JiraOptions>() ?? new();
var confluenceOptions = config.GetSection("Confluence").Get<ConfluenceOptions>() ?? new();
var reviewer = config["Reviewer"] ?? Environment.UserName;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var models = new ModelClients(aiOptions);
ConsoleUi.Banner("Incident triage (Microsoft Agent Framework)", $"Model: {models.Describe()}");

// ---- RAG: embeddings + knowledge index --------------------------------------------------------------
// Runbooks and past postmortems are embedded once at start-up; the repository is indexed per run by the
// ingest executor. With a real provider this is where the embedding cost goes, see InMemoryVectorIndex
// for caching and for swapping in a persistent vector database.
using var embeddings = models.CreateEmbeddingGenerator();
await using var index = new KnowledgeIndex(embeddings, triageOptions.Rag);
await index.InitializeAsync(ct);

var runbookChunks = await index.IndexMarkdownFolderAsync(Path.Combine(prompts.KnowledgeFolder, "runbooks"), "runbook", ct);
var postmortemChunks = await index.IndexMarkdownFolderAsync(Path.Combine(prompts.KnowledgeFolder, "postmortems"), "postmortem", ct);
ConsoleUi.Info($"Knowledge indexed ({index.Describe()}): {runbookChunks} runbook chunks, {postmortemChunks} postmortem chunks.");

// ---- Input ------------------------------------------------------------------------------------------
var request = Intake.Collect(cli, Path.Combine(prompts.SampleDataFolder, "reports"), triageOptions.DefaultRef);
if (request.Reports.Count == 0)
{
    ConsoleUi.Error("No reports to triage.");
    return 1;
}

var journal = new TriageJournal(request.RunId, triageOptions.OutputFolder);
journal.Record(JournalKind.RunStarted, reviewer, request.RunId, new { request.Repo, Reports = request.Reports.Select(r => new { r.Id, r.Text }), Model = models.Describe() });

// ---- Composition: tools, agents, workflow -----------------------------------------------------------
using var jiraHttp = JiraClient.CreateHttpClient(jiraOptions);
using var confluenceHttp = ConfluenceClient.CreateHttpClient(confluenceOptions);
var jira = new JiraClient(jiraOptions, jiraHttp);
var confluence = new ConfluenceClient(confluenceOptions, confluenceHttp);
var cloner = new RepositoryCloner(triageOptions, Path.Combine(prompts.SampleDataFolder, "demo-repo"));
var agents = new AgentFactory(models, prompts, index, jira, journal);

var workflow = TriageWorkflow.Build(agents, cloner, index, jira, confluence, journal, triageOptions.MaxReviewRounds);

if (cli.PrintMermaid)
{
    // WorkflowVisualizer renders the graph actually built (not a hand-drawn diagram that drifts).
    ConsoleUi.Heading("Workflow graph (mermaid)");
    Console.WriteLine(WorkflowVisualizer.ToMermaidString(workflow));
}

// ---- Run -------------------------------------------------------------------------------------------
// RunStreamingAsync starts the run and gives us a live event stream. Alternatives:
//   InProcessExecution.RunAsync(...)   -> non-streaming; inspect run.NewEvents / run.ResumeAsync(responses) between halts.
//   ...with a CheckpointManager        -> persist after every superstep; ResumeStreamingAsync(...) later, e.g. after
//                                         a reviewer replies tomorrow from a web page instead of this console.
ConsoleUi.Heading($"Run {request.RunId}");
await using var run = await InProcessExecution.RunStreamingAsync(workflow, request, cancellationToken: ct);

TriageReport? result = null;
await foreach (var evt in run.WatchStreamAsync(ct))
{
    switch (evt)
    {
        case TriageProgressEvent p:
            ConsoleUi.Step(p.Step, p.Message);
            break;

        // Human in the loop #1: review a root-cause hypothesis.
        case RequestInfoEvent { Request: var req } when req.TryGetDataAs<RootCauseReviewRequest>(out var review):
            await run.SendResponseAsync(req.CreateResponse(ReviewConsole.AskRootCause(review, reviewer, cli.AutoApprove)));
            break;

        // Human in the loop #2: approve publishing to Jira / Confluence.
        case RequestInfoEvent { Request: var req } when req.TryGetDataAs<PublishApprovalRequest>(out var publish):
            await run.SendResponseAsync(req.CreateResponse(ReviewConsole.AskPublish(publish, reviewer, cli.AutoApprove)));
            break;

        case WorkflowOutputEvent output when output.Is<TriageReport>(out var report):
            result = report;
            break;

        case ExecutorFailedEvent failed:
            ConsoleUi.Error($"Step '{failed.ExecutorId}' failed: {failed.Data?.Message}");
            break;

        case WorkflowErrorEvent error:
            ConsoleUi.Error($"Workflow error: {error.Exception?.Message}");
            break;
    }
}

if (result is null)
{
    ConsoleUi.Error($"The run ended without a result. See {journal.FilePath}.");
    return 2;
}

ReviewConsole.PrintSummary(result, journal);
return 0;
