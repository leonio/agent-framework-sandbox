using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdlcStudio.Api.Data;
using SdlcStudio.Api.Domain;
using SdlcStudio.Api.Orchestration;

namespace SdlcStudio.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for workflow runs.
/// </summary>
/// <remarks>
/// Written as a C# 14 <c>extension</c> block on <see cref="IEndpointRouteBuilder"/>. Functionally the same as
/// a classic <c>static</c> extension method; extension blocks let you group several members (methods,
/// properties) for one receiver type and read more like the type's own API.
/// WHY minimal APIs over controllers? Less ceremony, explicit routing in one place, and TypedResults give
/// compile-time checked responses that also drive the OpenAPI document.
/// </remarks>
public static class RunEndpoints
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapRunEndpoints()
        {
            var runs = app.MapGroup("/api/runs").WithTags("Workflow runs");

            runs.MapGet("/", ListAsync);
            runs.MapGet("/{id:guid}", GetAsync);
            runs.MapDelete("/{id:guid}", async (Guid id, RunOrchestrator o, CancellationToken ct) =>
            {
                await o.DeleteAsync(id, ct);
                return TypedResults.NoContent();
            });

            // Starting work returns 202 Accepted: the agents run in the background (PhaseWorker).
            runs.MapPost("/", async (StartRunRequest req, RunOrchestrator o, CancellationToken ct) =>
                Accepted(await o.StartNewIdeaAsync(req.Name, req.Idea, ct)));

            runs.MapPost("/import", async (ImportRunRequest req, RunOrchestrator o, CancellationToken ct) =>
                Accepted(await o.StartImportFromPathAsync(req.Name, req.Path, ct)));

            // multipart/form-data upload of a zipped code base. Antiforgery is disabled because this sample
            // has no cookies/auth; with cookie auth you would keep antiforgery on for form posts.
            runs.MapPost("/import/upload", async ([FromForm] string name, IFormFile file, RunOrchestrator o, CancellationToken ct) =>
                {
                    if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        throw new StudioValidationException("Upload a .zip of the application's source code.");
                    await using var stream = file.OpenReadStream();
                    return Accepted(await o.StartImportFromZipAsync(name, stream, ct));
                })
                .DisableAntiforgery();

            runs.MapPost("/{id:guid}/answer", async (Guid id, AnswerRequest req, RunOrchestrator o, CancellationToken ct) =>
                Accepted(await o.AnswerAsync(id, req.Answer, ct)));

            runs.MapPost("/{id:guid}/approve", async (Guid id, DecisionRequest req, RunOrchestrator o, CancellationToken ct) =>
                Accepted(await o.ApproveAsync(id, req.Reason, ct)));

            runs.MapPost("/{id:guid}/reject", async (Guid id, DecisionRequest req, RunOrchestrator o, CancellationToken ct) =>
                Accepted(await o.RejectAsync(id, req.Reason ?? "", ct)));

            runs.MapPost("/{id:guid}/revise", async (Guid id, RunOrchestrator o, CancellationToken ct) =>
                Accepted(await o.ReviseSpecAsync(id, ct)));

            runs.MapPost("/{id:guid}/retry", async (Guid id, RunOrchestrator o, CancellationToken ct) =>
                Accepted(await o.RetryAsync(id, ct)));

            runs.MapPost("/{id:guid}/findings/{findingId:long}", async (Guid id, long findingId, FindingDecisionRequest req, RunOrchestrator o, CancellationToken ct) =>
            {
                var f = await o.DecideFindingAsync(id, findingId, req.Accepted, req.Reason, ct);
                return TypedResults.Ok(new FindingDto(f.Id, f.Round, f.Reviewer, f.Severity, f.Title, f.Detail, f.Status, f.DecisionReason));
            });

            // Server-Sent Events (.NET 10 TypedResults.ServerSentEvents). The UI listens and refetches on change.
            runs.MapGet("/{id:guid}/events", (Guid id, RunEventHub hub, CancellationToken ct) =>
                TypedResults.ServerSentEvents(hub.SubscribeAsync(id, ct), eventType: "run"));
            app.MapGet("/api/events", (RunEventHub hub, CancellationToken ct) =>
                TypedResults.ServerSentEvents(hub.SubscribeAsync(Guid.Empty, ct), eventType: "run")).WithTags("Workflow runs");

            return app;
        }
    }

    private static Accepted<RunSummaryDto> Accepted(WorkflowRun run) =>
        TypedResults.Accepted($"/api/runs/{run.Id}", RunSummaryDto.From(run));

    private static async Task<Ok<List<RunSummaryDto>>> ListAsync(StudioDbContext db, CancellationToken ct)
    {
        var runs = await db.Runs.AsNoTracking().OrderByDescending(r => r.UpdatedAt).Take(100).ToListAsync(ct);
        return TypedResults.Ok(runs.Select(RunSummaryDto.From).ToList());
    }

    private static async Task<Results<Ok<RunDetailDto>, NotFound>> GetAsync(Guid id, StudioDbContext db, CancellationToken ct)
    {
        // AsSplitQuery: one SQL query per collection instead of one giant cartesian join.
        var run = await db.Runs.AsNoTracking().AsSplitQuery()
            .Include(r => r.Messages)
            .Include(r => r.Artifacts)
            .Include(r => r.Findings)
            .Include(r => r.Epics)
            .Include(r => r.Stories).ThenInclude(s => s.Tasks)
            .Include(r => r.Checkpoints)
            .Include(r => r.Decisions)
            .SingleOrDefaultAsync(r => r.Id == id, ct);
        if (run is null) return TypedResults.NotFound();

        var events = await db.Events.AsNoTracking().Where(e => e.RunId == id).OrderByDescending(e => e.Id).Take(200).ToListAsync(ct);

        return TypedResults.Ok(new RunDetailDto(
            RunSummaryDto.From(run),
            run.WorkspacePath,
            run.SourcePath,
            run.ReviewRound,
            BuildPipeline(run),
            [.. run.Messages.OrderBy(m => m.Id).Select(m => new MessageDto(m.Id, m.Role, m.Content, m.CreatedAt))],
            [.. run.Artifacts.OrderByDescending(a => a.Id).Select(a => new ArtifactDto(a.Id, a.Phase, a.Kind, a.Title, a.Content, a.RelativePath, a.Version, a.CreatedAt))],
            [.. run.Findings.OrderByDescending(f => f.Round).ThenBy(f => f.Id).Select(f => new FindingDto(f.Id, f.Round, f.Reviewer, f.Severity, f.Title, f.Detail, f.Status, f.DecisionReason))],
            [.. run.Epics.OrderBy(e => e.Id).Select(e => new EpicDto(e.Id, e.Key, e.Title, e.Description, e.ExternalId,
                [.. run.Stories.Where(s => s.EpicId == e.Id).OrderBy(s => s.Sprint).ThenBy(s => s.Id).Select(s => new StoryDto(
                    s.Id, s.Key, s.Title, s.UserStory, [.. s.AcceptanceCriteria.Split('\n', StringSplitOptions.RemoveEmptyEntries)],
                    s.Points, s.Sprint, s.Status, s.ExternalId,
                    [.. s.Tasks.OrderBy(t => t.Order).Select(t => new TaskDto(t.Id, t.Order, t.Title, t.Detail, t.Files, t.Status, t.CommitSha))]))]))],
            [.. run.Checkpoints.OrderBy(c => c.Id).Select(c => new CheckpointDto(c.Id, c.StoryId, c.Kind, c.Title, c.Detail, c.Branch, c.Sha, c.CreatedAt))],
            [.. run.Decisions.OrderBy(d => d.Id).Select(d => new DecisionDto(d.Id, d.Phase, d.Approved, d.Reason, d.CreatedAt))],
            [.. events.Select(e => new EventDto(e.Id, e.Phase, e.Level, e.Message, e.CreatedAt))]));
    }

    /// <summary>The stepper shown at the top of the workflow screen.</summary>
    private static List<PhaseStepDto> BuildPipeline(WorkflowRun run)
    {
        var pipeline = run.Source.Pipeline;
        var current = pipeline.ToList().IndexOf(run.Phase);
        return [.. pipeline.Select((phase, i) => new PhaseStepDto(phase, phase.DisplayName,
            run.Status == RunStatus.Completed || i < current ? "done"
            : i > current ? "upcoming"
            : run.Status switch
            {
                RunStatus.Failed => "failed",
                RunStatus.AwaitingInput or RunStatus.AwaitingApproval => "waiting",
                _ => "active",
            }))];
    }
}
