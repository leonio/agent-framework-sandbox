using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using SdlcStudio.Api.Tools;

namespace SdlcStudio.Api.Workflows;

// =============================================================================================
// Three workflows that all end in the same place, a published SpecBundle:
//
//   Design  : DesignRequest -> [spec-writer] -> [architect] -> [publish specs]
//   Import  : ImportRequest -> [scan code base] -> [app-analyst + file tools] -> [architect as-is] -> [publish specs]
//   Revise  : ReviseRequest -> [spec-reviser] -> [publish specs]
//
// The architect and publisher executors are reused, which is the point: small single-purpose
// steps compose into different pipelines. The "existing app" feature is just a different front
// half of the same graph; everything after it (review, plan, develop...) is identical.
// =============================================================================================

public sealed record DesignRequest(string Requirements);
public sealed record ImportRequest(string SourcePath, string? RepositoryHint);
public sealed record AnalysisRequest(string SourcePath, string Inventory, string RepositoryContext);
public sealed record SpecDraft(string FunctionalSpec, bool AsIs);
public sealed record ReviseRequest(SpecBundle Current, string Accepted, string Rejected);

public static class SpecWorkflows
{
    public static Workflow BuildDesign(RunContext run, AIAgent specWriter, AIAgent architect)
    {
        var writer = new SpecWriterExecutor(run, specWriter);
        var arch = new ArchitectExecutor(run, architect);
        var publish = new SpecPublisherExecutor(run);

        return new WorkflowBuilder(writer)
            .WithName("design")
            .WithDescription("Requirements to functional spec and technical design")
            .AddEdge(writer, arch)
            .AddEdge(arch, publish)
            .WithOutputFrom(publish)
            .Build();
    }

    public static Workflow BuildImport(RunContext run, AIAgent analyst, AIAgent architect, CodebaseTools codebase, GitHubMcpTools mcp)
    {
        var scan = new CodebaseScannerExecutor(run, codebase, mcp);
        var analyse = new AppAnalystExecutor(run, analyst);
        var arch = new ArchitectExecutor(run, architect);
        var publish = new SpecPublisherExecutor(run);

        return new WorkflowBuilder(scan)
            .WithName("import")
            .WithDescription("Existing code base to reverse-engineered specs")
            .AddEdge(scan, analyse)
            .AddEdge(analyse, arch)
            .AddEdge(arch, publish)
            .WithOutputFrom(publish)
            .Build();
    }

    public static Workflow BuildRevise(RunContext run, AIAgent reviser)
    {
        var revise = new SpecReviserExecutor(run, reviser);
        var publish = new SpecPublisherExecutor(run);

        return new WorkflowBuilder(revise)
            .WithName("revise")
            .AddEdge(revise, publish)
            .WithOutputFrom(publish)
            .Build();
    }
}

/// <summary>Agent step: agreed requirements -> functional spec (markdown).</summary>
internal sealed class SpecWriterExecutor(RunContext run, AIAgent agent) : Executor<DesignRequest, SpecDraft>("spec-writer")
{
    public override async ValueTask<SpecDraft> HandleAsync(DesignRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        // RAG OPPORTUNITY: retrieve similar past specs / your organisation's product standards and add
        // them to the prompt (or via an AIContextProvider) so new specs follow established conventions.
        var response = await agent.RunAsync(run.Prompt("design-spec", ("requirements", message.Requirements)), cancellationToken: ct);
        return new SpecDraft(response.Text, AsIs: false);
    }
}

/// <summary>Agent step: functional spec -> technical design. Used by design and import.</summary>
internal sealed class ArchitectExecutor(RunContext run, AIAgent agent) : Executor<SpecDraft, SpecBundle>("architect")
{
    public override async ValueTask<SpecBundle> HandleAsync(SpecDraft message, IWorkflowContext context, CancellationToken ct = default)
    {
        // RAG OPPORTUNITY: architecture decision records (ADRs) and approved technology lists are a great
        // retrieval source here, so designs reuse the stack your company already runs.
        var prompt = run.Prompt("technical-design", ("spec", message.FunctionalSpec), ("mode", message.AsIs ? "as-is" : "new"));
        var response = await agent.RunAsync(prompt, cancellationToken: ct);
        return new SpecBundle(message.FunctionalSpec, response.Text);
    }
}

/// <summary>Plain code step: writes the spec documents to the run's workspace folder.</summary>
[YieldsOutput(typeof(SpecBundle))]
internal sealed class SpecPublisherExecutor(RunContext run) : Executor<SpecBundle>("publish-specs")
{
    public override async ValueTask HandleAsync(SpecBundle message, IWorkflowContext context, CancellationToken ct = default)
    {
        Directory.CreateDirectory(run.SpecsFolder);
        await File.WriteAllTextAsync(Path.Combine(run.SpecsFolder, "functional-spec.md"), message.FunctionalSpec, ct);
        await File.WriteAllTextAsync(Path.Combine(run.SpecsFolder, "technical-design.md"), message.TechnicalDesign, ct);

        await context.AddEventAsync(new StudioProgressEvent($"Specs written to {Path.GetRelativePath(run.WorkspacePath, run.SpecsFolder)}/"), ct);
        await context.YieldOutputAsync(message, ct);
    }
}

/// <summary>
/// Plain code step: a deterministic inventory of the code base before any model is involved.
/// WHY: it is free, fast and reliable, and it gives the analyst agent a map so it spends its tool
/// calls (and tokens) reading the right files.
/// </summary>
internal sealed class CodebaseScannerExecutor(RunContext run, CodebaseTools codebase, GitHubMcpTools mcp)
    : Executor<ImportRequest, AnalysisRequest>("scan-codebase")
{
    public override async ValueTask<AnalysisRequest> HandleAsync(ImportRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        var inventory = codebase.BuildInventory();
        var fileCount = inventory.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        await context.AddEventAsync(new StudioProgressEvent($"Scanned {fileCount} files in the existing app"), ct);

        // MCP: when the app lives on GitHub, repository context (recent commits, open PRs) comes from the
        // GitHub MCP server. Mocked here; see Tools/GitHubMcpTools.cs for the real client code.
        var repoContext = await mcp.GetRepositoryContextAsync(message.RepositoryHint ?? run.Project, ct);
        await context.AddEventAsync(new StudioProgressEvent("Fetched repository context via (mocked) GitHub MCP tools"), ct);

        return new AnalysisRequest(message.SourcePath, inventory, repoContext);
    }
}

/// <summary>Agent step with tools: the analyst explores the code base itself and writes a spec.</summary>
internal sealed class AppAnalystExecutor(RunContext run, AIAgent agent) : Executor<AnalysisRequest, SpecDraft>("app-analyst")
{
    public override async ValueTask<SpecDraft> HandleAsync(AnalysisRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        var prompt = run.Prompt("import-analyse", ("inventory", $"{message.Inventory}\n\nRepository context:\n{message.RepositoryContext}"));

        // The agent was created with list_files / read_file tools. ChatClientAgent runs the
        // function-calling loop for us: model asks for a tool -> we execute it -> result goes back
        // to the model -> repeat until it answers with text.
        var response = await agent.RunAsync(prompt, cancellationToken: ct);

        var toolCalls = response.Messages.SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionCallContent>().ToList();
        foreach (var call in toolCalls)
            await context.AddEventAsync(new StudioProgressEvent($"Tool call {call.Name}({string.Join(", ", call.Arguments?.Values ?? [])})"), ct);

        return new SpecDraft(response.Text, AsIs: true);
    }
}

/// <summary>Agent step: applies the findings a human accepted, honouring the reasons they gave.</summary>
internal sealed class SpecReviserExecutor(RunContext run, AIAgent agent) : Executor<ReviseRequest, SpecBundle>("spec-reviser")
{
    public override async ValueTask<SpecBundle> HandleAsync(ReviseRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        var prompt = run.Prompt("revise-spec",
            ("spec", message.Current.FunctionalSpec), ("accepted", message.Accepted), ("rejected", message.Rejected));
        var response = await agent.RunAsync(prompt, cancellationToken: ct);

        // Only the functional spec is revised; the design carries over. ALTERNATIVE: send the revised
        // spec back through the ArchitectExecutor so the design stays in sync (one extra edge).
        return message.Current with { FunctionalSpec = response.Text };
    }
}
