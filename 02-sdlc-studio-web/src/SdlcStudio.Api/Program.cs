// =============================================================================================
// SDLC Studio: an ASP.NET Core (.NET 10) minimal API that drives a multi-agent software delivery
// workflow built with the Microsoft Agent Framework, plus a React UI (src/SdlcStudio.Web).
//
// Request flow:  React UI -> minimal API -> RunOrchestrator (human decisions, state machine)
//                -> PhaseJobQueue -> PhaseWorker (background) -> PhaseJobRunner
//                -> Agent Framework Workflow (executors + agents + tools) -> database + workspace files
//                -> RunEventHub -> Server-Sent Events -> UI refreshes
// =============================================================================================

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using SdlcStudio.Api.Ai;
using SdlcStudio.Api.Data;
using SdlcStudio.Api.Endpoints;
using SdlcStudio.Api.Options;
using SdlcStudio.Api.Orchestration;
using SdlcStudio.Api.Tools;
using SdlcStudio.Api.Workflows;

var builder = WebApplication.CreateBuilder(args);

// ----- Configuration -------------------------------------------------------------------------
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.Section));
builder.Services.Configure<StudioOptions>(builder.Configuration.GetSection(StudioOptions.Section));

// ----- Data ----------------------------------------------------------------------------------
builder.Services.AddStudioDatabase(builder.Configuration);

// ----- AI: chat clients, agents, prompts -----------------------------------------------------
// Lifetimes: the chat client factory is a singleton (it caches the real HTTP client); everything that
// touches the DbContext is scoped to one request or one background job.
builder.Services.AddSingleton<ChatClientFactory>();
builder.Services.AddScoped<PromptLibrary>();
builder.Services.AddScoped<AgentFactory>();
builder.Services.AddScoped<WorkflowRunner>();

// ----- Tools / integrations ------------------------------------------------------------------
// Typed HttpClients: IHttpClientFactory manages handler lifetimes (no socket exhaustion, DNS refresh).
// ALTERNATIVE: add Microsoft.Extensions.Http.Resilience and call .AddStandardResilienceHandler() for retries.
builder.Services.AddHttpClient<JiraClient>();
builder.Services.AddHttpClient<ConfluenceClient>();
builder.Services.AddSingleton<GitWorkspace>();
builder.Services.AddSingleton<MergeRequestPublisher>();
builder.Services.AddSingleton<GitHubMcpTools>();

// ----- Orchestration -------------------------------------------------------------------------
builder.Services.AddSingleton<PhaseJobQueue>();
builder.Services.AddSingleton<RunEventHub>();
builder.Services.AddSingleton<RunJournal>();
builder.Services.AddScoped<RunOrchestrator>();
builder.Services.AddScoped<PhaseJobRunner>();
builder.Services.AddHostedService<PhaseWorker>();

// ----- Web -----------------------------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddValidation();          // .NET 10: DataAnnotations validation for minimal API parameters
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();             // GET /openapi/v1.json
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

await app.InitializeStudioDatabaseAsync();

// Map domain exceptions to ProblemDetails responses. ALTERNATIVE: IExceptionHandler implementations.
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = error switch
    {
        StudioValidationException => (StatusCodes.Status400BadRequest, "Invalid request"),
        StudioNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
    };
    context.Response.StatusCode = status;
    await Results.Problem(title: title, detail: error?.Message, statusCode: status).ExecuteAsync(context);
}));

app.UseCors();
app.MapOpenApi();

app.MapRunEndpoints();
app.MapAdminEndpoints();

// Serve the built React app (pnpm build copies it to wwwroot) so one `dotnet run` hosts everything.
// During development run `pnpm dev` instead and Vite proxies /api to this server.
app.UseDefaultFiles();
app.MapStaticAssets();
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
