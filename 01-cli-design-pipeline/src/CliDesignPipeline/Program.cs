// =============================================================================================
//  CLI design pipeline - Microsoft Agent Framework sample
//
//  idea ─▶ grill-me interview (human answers) ─▶ spec approval (human) ─▶ developer agent
//       ─▶ tester agent ─(rework loop)─▶ merge-request agent ─▶ git commands + Jira/Confluence
//
//  Run offline:   dotnet run -- "A tiny task tracker for my team" --auto
//  Run for real:  dotnet user-secrets set Pipeline:ApiKey <key>
//                 dotnet run -- "..." --Pipeline:Provider=OpenAI
//
//  Reading order for reviewers: this file -> Workflow/DesignPipelineWorkflow.cs ->
//  Workflow/Executors/* -> Agents/PipelineAgentFactory.cs -> Tools/* -> Rag/*.
// =============================================================================================

using System.Net.Http.Headers;
using System.Text;
using CliDesignPipeline.Agents;
using CliDesignPipeline.Ai;
using CliDesignPipeline.Cli;
using CliDesignPipeline.Configuration;
using CliDesignPipeline.Content;
using CliDesignPipeline.Contracts;
using CliDesignPipeline.Tools;
using CliDesignPipeline.Tools.Integrations;
using CliDesignPipeline.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// --- 1. Arguments -----------------------------------------------------------------------------
// Tiny hand-rolled parsing: an optional positional idea and an --auto switch; everything else is
// passed to the configuration system as --Section:Key=value. Alternative: System.CommandLine,
// worth it once there are sub-commands (e.g. `design`, `resume <run>`, `list`).
string[] configArgs = [.. args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a != "--auto")];
if (args.Contains("--auto"))
{
    configArgs = [.. configArgs, "--Pipeline:AutoAnswer=true"];
}

var idea = args is [var first, ..] && !first.StartsWith('-') ? first : null;

// --- 2. Host: configuration + DI + logging --------------------------------------------------
// We use the generic host for its plumbing (appsettings + user-secrets + env vars + command line,
// DI, logging, IHttpClientFactory) but never call host.Run(): a CLI that does one job and exits
// does not need hosted services or a lifetime. For a long-running worker, register PipelineRunner
// as a BackgroundService instead.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = configArgs,
    // appsettings.json and the prompts/ skills/ instructions/ folders are copied next to the
    // binary; resolve them from there so `dotnet run --project ...` works from any folder.
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddUserSecrets<Program>(optional: true);   // dev-time secrets, outside the repo
builder.Configuration.AddEnvironmentVariables("PIPELINE_");        // e.g. PIPELINE_Pipeline__ApiKey

builder.Services.AddOptions<PipelineOptions>().Bind(builder.Configuration.GetSection(PipelineOptions.SectionName));
builder.Services.AddOptions<IntegrationOptions>().Bind(builder.Configuration.GetSection(IntegrationOptions.SectionName));

var pipelineOptions = builder.Configuration.GetSection(PipelineOptions.SectionName).Get<PipelineOptions>() ?? new();
var ui = new ConsoleUi();

if (idea is null)
{
    idea = pipelineOptions.AutoAnswer
        ? "A tiny command-line task tracker for my team"
        : ui.Ask("What would you like to build? ");
}

if (string.IsNullOrWhiteSpace(idea))
{
    ui.Error("No idea, no app. Pass one as the first argument or type it at the prompt.");
    return 2;
}

// Content and per-run singletons.
builder.Services.AddSingleton(ui);
builder.Services.AddSingleton(new PromptLibrary(AppContext.BaseDirectory));
builder.Services.AddSingleton(sp => new RunWorkspace(
    Path.GetFullPath(sp.GetRequiredService<IOptions<PipelineOptions>>().Value.OutputRoot, Environment.CurrentDirectory),
    idea));

// AI plumbing.
builder.Services.AddSingleton<ChatClientFactory>();
builder.Services.AddSingleton<PipelineAgentFactory>();

// Tools.
builder.Services.AddSingleton<WorkspaceTools>();
builder.Services.AddTransient<TrackerTools>();
builder.Services.AddTransient<GitHubTools>();

// Typed HTTP clients for the integrations. In mock mode the *primary handler* is swapped for an
// in-process fake; everything above it (client code, tool wrapper, model tool calls) is real.
builder.Services.AddHttpClient<JiraClient>((sp, http) => ConfigureAtlassian(http, sp.GetRequiredService<IOptions<IntegrationOptions>>().Value.Jira))
    .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<IOptions<IntegrationOptions>>().Value.Jira.UseMock
        ? new FakeAtlassianHandler()
        : new SocketsHttpHandler());
builder.Services.AddHttpClient<ConfluenceClient>((sp, http) => ConfigureAtlassian(http, sp.GetRequiredService<IOptions<IntegrationOptions>>().Value.Confluence))
    .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<IOptions<IntegrationOptions>>().Value.Confluence.UseMock
        ? new FakeAtlassianHandler()
        : new SocketsHttpHandler());
// Resilience: add .AddStandardResilienceHandler() (Microsoft.Extensions.Http.Resilience) for
// retries with jitter, timeouts and a circuit breaker before pointing these at real services.

// Workflow.
builder.Services.AddTransient<DesignPipelineWorkflow>();
builder.Services.AddTransient<PipelineRunner>();
builder.Services.AddSingleton<IStakeholder>(sp => sp.GetRequiredService<IOptions<PipelineOptions>>().Value.AutoAnswer
    ? new AutoStakeholder(ui)
    : new ConsoleStakeholder(ui));

using var host = builder.Build();

// --- 3. Run ---------------------------------------------------------------------------------
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var options = host.Services.GetRequiredService<IOptions<PipelineOptions>>().Value;
ui.Info($"Provider: {options.Provider}{(options.Provider is ModelProvider.Mock ? " (offline, scripted)" : $" / {options.Model}")}" +
        $" | interview budget: {options.MaxInterviewQuestions} | review rounds: {options.MaxReviewRounds}");

try
{
    return await host.Services.GetRequiredService<PipelineRunner>()
        .RunAsync(new DesignBrief(idea, options.WorkItemKey), cts.Token);
}
catch (OperationCanceledException)
{
    ui.Warn("Cancelled.");
    return 130;
}

static void ConfigureAtlassian(HttpClient http, AtlassianOptions o)
{
    http.BaseAddress = o.BaseUrl;
    // Atlassian Cloud basic auth = email + API token (https://id.atlassian.com/manage-profile/security/api-tokens).
    // For apps acting on behalf of users, use OAuth 2.0 (3LO) instead.
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.Email}:{o.ApiToken}")));
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
}
