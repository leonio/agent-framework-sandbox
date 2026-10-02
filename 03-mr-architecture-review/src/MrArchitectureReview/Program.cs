// =================================================================================================
// MR architecture review: a Microsoft Agent Framework workflow sample.
//
// Several small agents review a pull request (design, security, extensibility) in parallel, a human
// accepts or rejects each finding with a reason, everything is stored, and a publisher agent records
// the outcome in Jira / GitHub / Confluence (dry-run by default).
//
//   dotnet run                                   # offline: bundled sample PR + scripted model
//   dotnet run -- --auto                         # same, non-interactive triage
//   dotnet run -- --pr https://github.com/o/r/pull/123 --provider OpenAI
//   dotnet run -- --graph                        # print the workflow as Mermaid and exit
//
// Start reading at Orchestration/ReviewWorkflowFactory.cs; it declares the whole graph.
// =================================================================================================

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrArchitectureReview.Configuration;
using MrArchitectureReview.Domain;
using MrArchitectureReview.Hosting;
using MrArchitectureReview.Orchestration;

// Bare flags are pulled out first; everything else goes to the configuration system, so any setting
// can be overridden from the command line, e.g. --Review:Settings:MinimumConfidence=0.6
HashSet<string> flags = [.. args.Where(a => a is "--auto" or "--graph")];
string[] configArgs = [.. args.Where(a => !flags.Contains(a))];

// The generic host gives us configuration, DI and logging with the same conventions as ASP.NET Core.
// WHY bother in a console app? Every service below is constructed the same way in the tests and would
// be in a web host (sample 02). Alternative: `new` everything in Program.cs; shorter, but it doesn't scale
// past a handful of services and makes swapping implementations (mock vs real) a code change.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = configArgs,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Configuration.AddCommandLine(configArgs, new Dictionary<string, string>
{
    ["--pr"] = "Source",
    ["--provider"] = $"{ReviewOptions.SectionName}:Ai:Provider",
    ["--model"] = $"{ReviewOptions.SectionName}:Ai:Model",
});
// User secrets are only added automatically in the Development environment; add them explicitly so
// `dotnet user-secrets set Review:Ai:ApiKey ...` works however the app is launched.
builder.Configuration.AddUserSecrets<ReviewOptions>(optional: true);

builder.Services.AddOptions<ReviewOptions>()
    .Bind(builder.Configuration.GetSection(ReviewOptions.SectionName))
    .PostConfigure(o => o.AutoTriage |= flags.Contains("--auto"));

builder.Logging.ClearProviders().AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = false;
    o.TimestampFormat = "HH:mm:ss ";
});

// Every service (model access, PR sources, memory, RAG, integrations, the workflow) is registered in
// one extension method so the tests build exactly the same object graph. Read it next.
builder.Services.AddMrArchitectureReview();

using var host = builder.Build();

if (flags.Contains("--graph"))
{
    Console.WriteLine(host.Services.GetRequiredService<ReviewWorkflowFactory>().Build().ToMermaidString());
    return 0;
}

var options = host.Services.GetRequiredService<IOptions<ReviewOptions>>().Value;
var source = builder.Configuration["Source"] ?? "sample";

Console.WriteLine($"MR architecture review · source: {source} · model: {options.Ai.Provider}" +
                  $"{(options.Ai.Provider is AiProvider.Mock ? " (offline, scripted)" : $"/{options.Ai.Model}")}" +
                  $" · integrations: {(options.Integrations.DryRun ? "dry-run" : "LIVE")}");

// Ctrl+C cancels the run cleanly instead of killing the process mid-write.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var outcome = await host.Services.GetRequiredService<ReviewRunner>().RunAsync(new ReviewRequest(source), cts.Token);

Console.WriteLine();
Console.WriteLine($"Done. {outcome.Record.AcceptedCount} accepted, {outcome.Record.RejectedCount} rejected, " +
                  $"{outcome.Record.Findings.Count} total.");
Console.WriteLine($"Record: {outcome.RecordPath}");
Console.WriteLine($"Publisher: {outcome.PublisherSummary}");
return 0;
