using System.Diagnostics;
using A2A;
using A2A.AspNetCore;
using FleetSpike.Shared;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using TeamB.SecretScanner;

// TEAM B: a rules-based scanner. No model, no prompt, no keys. Team B returns A2A *messages* (the default), because its
// answer is instant. Same contract shape as Team A, completely different inside.

const string Name = "secret-scanner";
const string A2APath = "/a2a/secret-scanner";

ActivitySource.AddActivityListener(new ActivityListener
{
    ShouldListenTo = _ => true,
    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
});

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddFleetAuth();
builder.Services.UseClaimsBasedAgentIsolation();

string publicBase = builder.Configuration["PUBLIC_BASE_URL"] ?? "http://localhost:5102";

AgentContractDocument contract = Contracts.Build(
    name: Name,
    owner: "team-b",
    description: "Finds committed secrets (keys, tokens, private keys, passwords) in the added lines of a diff.",
    version: "0.9.2",
    runtime: "rules",
    risk: ["read"],
    publicBaseUrl: publicBase,
    a2aPath: A2APath,
    input: typeof(ChangeReviewInput),
    output: typeof(Findings),
    outputKind: "findings");
AgentCard card = Contracts.BuildCard(contract, publicBase, A2APath, streaming: false);

builder.AddAIAgent(Name, (_, _) => new SecretScannerAgent()).AddA2AServer();

WebApplication app = builder.Build();

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/a2a"))
    {
        app.Logger.LogInformation(
            "a2a {Method} {Path} trace={TraceId} traceparent={Traceparent}",
            ctx.Request.Method, ctx.Request.Path, Activity.Current?.TraceId.ToString() ?? "none", ctx.Request.Headers["traceparent"].ToString());
    }

    await next();
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => "ok");
app.MapGet("/.well-known/agent-contract.json", () => Results.Json(contract, ContractJson.Options));
app.MapWellKnownAgentCard(card);
app.MapHttpA2A(app.Services.GetRequiredKeyedService<A2AServer>(Name), card, A2APath).RequireAuthorization();

app.Run();
