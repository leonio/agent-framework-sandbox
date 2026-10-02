using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Roster.Agents;
using Roster.Api.Auth;
using Roster.Platform.Data;
using Roster.Platform.Models;
using Roster.Platform.Scenarios;
using Roster.Platform.Sources;

namespace Roster.Api.Endpoints;

/// <summary>
/// Assignments: start one, list them, watch one (phases, findings, the ledger), cancel it, and triage its findings.
/// </summary>
/// <remarks>
/// <para><b>Who sees what.</b> The owner sees and acts on their assignments. Admins can read everyone's (the realm
/// describes the role that way) but only the owner decides findings or cancels.</para>
/// <para><b>Endpoint choice.</b> The endpoint and model picked for the assignment, and any per-phase or per-step
/// overrides from the advanced panel, are checked here (shared or yours) so a bad pick fails now with a clear message
/// instead of later in a runner. The model router checks again when the agents run.</para>
/// </remarks>
public static class AssignmentEndpoints
{
    public sealed record NewAssignmentInput(
        string Source,
        string? Title = null,
        string? Scenario = null,
        Guid? EndpointId = null,
        string? Model = null,
        AssignmentOverrides? Overrides = null);

    public sealed record AssignmentSummary(
        Guid Id, string Title, string Scenario, string Source, AssignmentState State, string OwnerId,
        DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, int Findings, int Undecided);

    public sealed record PhaseView(string Key, int Order, PhaseState State, int Attempt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Error);

    public sealed record FindingView(
        Guid Id, string Agent, string AgentHash, int Rank, string Title, string Detail, string Recommendation, string Severity,
        string? FilePath, double Confidence, FindingDecision Decision, string? Reason, string? DecidedBy, DateTimeOffset? DecidedAt, Guid InvocationId);

    public sealed record StepView(
        Guid Id, string Phase, string Step, int Attempt, string Agent, string AgentHash, string Endpoint, string Model,
        string Strategy, string? Outcome, long? InputTokens, long? OutputTokens, long? DurationMs, DateTimeOffset StartedAt);

    public sealed record AssignmentDetail(
        AssignmentSummary Assignment, Guid? EndpointId, string? Model, JsonElement Overrides, string? Error, bool CancelRequested,
        IReadOnlyList<PhaseView> Phases, IReadOnlyList<FindingView> Findings, IReadOnlyList<StepView> Steps, Guid? RetroSessionId);

    public sealed record StepDetail(
        StepView Step, string Input, JsonElement? Output, string? RawText, string? Error, JsonElement? ToolCalls, string? Reasoning, string? TraceId);

    public sealed record DecisionInput(FindingDecision Decision, string? Reason);

    public static IEndpointRouteBuilder MapAssignmentEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/assignments").WithTags("Assignments");

        group.MapPost("/", async (NewAssignmentInput input, ClaimsPrincipal user, RosterDb db, ScenarioEngine engine,
            IEnumerable<IPullRequestSource> sources, CancellationToken cancellationToken) =>
        {
            string me = user.UserId();
            string source = CredentialEndpoints.Required(input.Source, "source", maxLength: 500);
            if (!sources.Any(s => s.CanHandle(source)))
            {
                throw new ArgumentException("The source must be fixture:sample-pr or a GitHub pull request URL.");
            }

            AssignmentOverrides overrides = input.Overrides ?? new AssignmentOverrides(null, null);
            IEnumerable<Guid> chosen = new[] { input.EndpointId }
                .Concat(overrides.Phases?.Values.Select(c => c.EndpointId) ?? [])
                .Concat(overrides.Steps?.Values.Select(c => c.EndpointId) ?? [])
                .OfType<Guid>()
                .Distinct();
            foreach (Guid endpointId in chosen)
            {
                if (!await db.Endpoints.AnyAsync(e => e.Id == endpointId && (e.OwnerId == null || e.OwnerId == me), cancellationToken))
                {
                    throw new ArgumentException($"Endpoint {endpointId} is not a shared endpoint or one of yours.");
                }
            }

            Assignment assignment = await engine.CreateAssignmentAsync(new NewAssignment(
                OwnerId: me,
                Scenario: input.Scenario ?? PrReviewScenario.ScenarioKey,
                Title: string.IsNullOrWhiteSpace(input.Title) ? DefaultTitle(source) : input.Title.Trim(),
                Source: source,
                EndpointId: input.EndpointId,
                Model: string.IsNullOrWhiteSpace(input.Model) ? null : input.Model.Trim(),
                OverridesJson: JsonSerializer.Serialize(overrides, ContractJson.Options)), cancellationToken);

            return TypedResults.Created($"/api/assignments/{assignment.Id}", await SummaryAsync(db, assignment.Id, cancellationToken));
        });

        // Newest first. ?all=true lets an admin see everyone's.
        group.MapGet("/", async (bool? all, ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken) =>
        {
            string me = user.UserId();
            bool everyone = all == true && user.IsAdmin();
            return await Summaries(db)
                .Where(a => everyone || a.OwnerId == me)
                .OrderByDescending(a => a.CreatedAt)
                .Take(200)
                .ToListAsync(cancellationToken);
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken) =>
        {
            Assignment assignment = await ReadableAsync(db, id, user, cancellationToken);
            List<Phase> phases = await db.Phases.AsNoTracking().Where(p => p.AssignmentId == id).OrderBy(p => p.Order).ToListAsync(cancellationToken);
            List<Finding> findings = await db.Findings.AsNoTracking().Where(f => f.AssignmentId == id)
                .OrderBy(f => f.AgentName).ThenBy(f => f.Rank).ToListAsync(cancellationToken);
            List<Invocation> steps = await db.Invocations.AsNoTracking().Where(i => i.AssignmentId == id).OrderBy(i => i.StartedAt).ToListAsync(cancellationToken);
            Guid? retro = await db.RetroSessions.Where(s => s.AssignmentId == id).Select(s => (Guid?)s.Id).SingleOrDefaultAsync(cancellationToken);

            return new AssignmentDetail(
                (await SummaryAsync(db, id, cancellationToken))!,
                assignment.EndpointId,
                assignment.Model,
                JsonDocument.Parse(assignment.OverridesJson).RootElement,
                assignment.Error,
                assignment.CancelRequested,
                [.. phases.Select(p => new PhaseView(p.Key, p.Order, p.State, p.Attempt, p.StartedAt, p.CompletedAt, p.Error))],
                [.. findings.Select(ToView)],
                [.. steps.Select(ToView)],
                retro);
        });

        // The ledger view of one step: exactly what the model was given and what it did.
        group.MapGet("/{id:guid}/steps/{stepId:guid}", async (Guid id, Guid stepId, ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken) =>
        {
            await ReadableAsync(db, id, user, cancellationToken);
            Invocation step = await db.Invocations.AsNoTracking().SingleOrDefaultAsync(i => i.Id == stepId && i.AssignmentId == id, cancellationToken)
                ?? throw new KeyNotFoundException($"No step {stepId} in this assignment.");

            return new StepDetail(
                ToView(step),
                step.InputText,
                step.OutputJson is { } output ? JsonDocument.Parse(output).RootElement : null,
                step.RawText,
                step.Error,
                step.ToolCallsJson is { } calls ? JsonDocument.Parse(calls).RootElement : null,
                step.Reasoning,
                step.TraceId);
        });

        group.MapPost("/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, ScenarioEngine engine, CancellationToken cancellationToken) =>
            await engine.RequestCancelAsync(id, user.UserId(), cancellationToken) ? Results.Accepted() : Results.NotFound());

        // Triage. A rejection needs a reason (the platform enforces it); the last decision completes the assignment.
        app.MapPost("/api/findings/{id:guid}/decision", async (Guid id, DecisionInput input, ClaimsPrincipal user, FindingDecisions decisions,
            CancellationToken cancellationToken) => ToView(await decisions.DecideAsync(id, user.UserId(), input.Decision, input.Reason, cancellationToken)))
            .WithTags("Assignments");

        return app;
    }

    /// <summary>Loads an assignment the person may read (owner, or any admin). Others get a 403.</summary>
    internal static async Task<Assignment> ReadableAsync(RosterDb db, Guid id, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        Assignment assignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"No assignment {id}.");
        return assignment.OwnerId == user.UserId() || user.IsAdmin()
            ? assignment
            : throw new UnauthorizedAccessException("This assignment belongs to someone else.");
    }

    private static IQueryable<AssignmentSummary> Summaries(RosterDb db) =>
        db.Assignments.AsNoTracking().Select(a => new AssignmentSummary(
            a.Id, a.Title, a.Scenario, a.Source, a.State, a.OwnerId, a.CreatedAt, a.CompletedAt,
            db.Findings.Count(f => f.AssignmentId == a.Id),
            db.Findings.Count(f => f.AssignmentId == a.Id && f.Decision == FindingDecision.Pending)));

    private static Task<AssignmentSummary?> SummaryAsync(RosterDb db, Guid id, CancellationToken cancellationToken) =>
        Summaries(db).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    private static string DefaultTitle(string source) =>
        source.Equals(FixturePullRequestSource.SamplePr, StringComparison.OrdinalIgnoreCase) ? "Sample pull request (payments)" : source;

    private static FindingView ToView(Finding f) => new(
        f.Id, f.AgentName, f.AgentHash, f.Rank, f.Title, f.Detail, f.Recommendation, f.Severity, f.FilePath, f.Confidence,
        f.Decision, f.DecisionReason, f.DecidedByTitle is null ? f.DecidedByName : $"{f.DecidedByName} · {f.DecidedByTitle}", f.DecidedAt, f.InvocationId);

    private static StepView ToView(Invocation i) => new(
        i.Id, i.PhaseKey, i.StepKey, i.Attempt, i.AgentName, i.AgentHash, i.EndpointName, i.Model, i.OutputStrategy,
        i.Outcome, i.InputTokens, i.OutputTokens, i.DurationMs, i.StartedAt);
}
