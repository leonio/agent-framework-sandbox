using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Roster.Agents;
using Roster.Platform.Data;

namespace Roster.Platform.Tools;

/// <summary>
/// The tools behind the <c>assignment.read</c> capability: read-only views of one assignment for the retro facilitator.
/// The names and shapes match what the facilitator's <c>AGENT.md</c> tells it to expect.
/// </summary>
/// <remarks>
/// <para><b>Scope.</b> A set of tools is built for one agent call (<see cref="AgentRunContext"/>) and only ever reads
/// that call's assignment, and only if the person the call runs for owns it. Step ids from the model are checked
/// against the assignment too, so it cannot read someone else's steps by guessing an id.</para>
/// <para><b>What it leaves out.</b> The facilitator's own turns (phase <c>retro</c>) are not steps worth discussing, so
/// they are hidden from the timeline and the step list.</para>
/// <para>Everything returned is data about what happened. The facilitator's instructions say so, and the text from
/// pull requests inside step inputs is still inside its untrusted fences.</para>
/// </remarks>
internal sealed class AssignmentTools(IDbContextFactory<RosterDb> dbs, AgentRunContext context)
{
    private const string RetroPhase = "retro";
    private const int MaxTextLength = 6_000;

    public IEnumerable<AITool> Create() =>
    [
        AIFunctionFactory.Create(GetTimelineAsync, "get_timeline",
            "The moments in this assignment worth talking about: findings the person rejected or accepted and their reasons, " +
            "retries and failures, slow or costly steps. Start here."),
        AIFunctionFactory.Create(ListStepsAsync, "list_steps",
            "Every agent step in the assignment: step id, phase, agent and version, model, outcome, duration and tokens."),
        AIFunctionFactory.Create(GetStepAsync, "get_step",
            "One step in detail: its rendered input, its output, the tools it called, tokens and duration."),
        AIFunctionFactory.Create(GetFindingsAsync, "get_findings",
            "Every finding, what the person decided about it, and their reason."),
        AIFunctionFactory.Create(GetReasoningAsync, "get_reasoning",
            "The model's reasoning for one step, if the endpoint returned any."),
        AIFunctionFactory.Create(GetChatAsync, "get_chat",
            "The conversation of a conversational step, if the step was one."),
    ];

    private async Task<List<string>> GetTimelineAsync(CancellationToken cancellationToken)
    {
        await using RosterDb db = await OpenAsync(cancellationToken);
        List<Finding> findings = await db.Findings.AsNoTracking().Where(f => f.AssignmentId == context.AssignmentId).ToListAsync(cancellationToken);
        List<Invocation> steps = await Steps(db).ToListAsync(cancellationToken);

        var moments = new List<string>();

        // Rejections first: the person's reasons are what the retro is for.
        moments.AddRange(findings.Where(f => f.Decision == FindingDecision.Rejected).Select(f =>
            $"You rejected {f.AgentName}'s finding \"{f.Title}\" ({f.Severity}). Your reason: \"{f.DecisionReason}\""));

        // Then anything that went wrong.
        foreach (Invocation step in steps.Where(s => s.Outcome is not "succeeded"))
        {
            moments.Add(step.Outcome switch
            {
                "repaired" => $"{step.AgentName}'s first reply was unusable, so it was asked once to repair it, which worked.",
                null => $"{step.AgentName} was interrupted before it finished.",
                _ => $"{step.AgentName} ended {step.Outcome}: {Shorten(step.Error, 200)}",
            });
        }

        moments.AddRange(findings.Where(f => f.Decision == FindingDecision.Accepted).Select(f =>
            $"You accepted {f.AgentName}'s finding \"{f.Title}\" ({f.Severity})" + (f.DecisionReason is { Length: > 0 } r ? $". Your reason: \"{r}\"" : ".")));

        int undecided = findings.Count(f => f.Decision == FindingDecision.Pending);
        if (undecided > 0)
        {
            moments.Add($"{undecided} finding(s) were left undecided.");
        }

        // Slow and costly steps, when there is something to compare.
        if (steps.Count >= 2)
        {
            Invocation slowest = steps.MaxBy(s => s.DurationMs ?? 0)!;
            moments.Add($"{slowest.AgentName} was the slowest step at {(slowest.DurationMs ?? 0) / 1000.0:F1} s.");
            Invocation costliest = steps.MaxBy(s => (s.InputTokens ?? 0) + (s.OutputTokens ?? 0))!;
            moments.Add($"{costliest.AgentName} used the most tokens: {costliest.InputTokens ?? 0} in, {costliest.OutputTokens ?? 0} out.");
        }

        return moments.Count == 0 ? ["Nothing notable happened yet: no decisions, no failures."] : [.. moments.Take(12)];
    }

    private async Task<List<object>> ListStepsAsync(CancellationToken cancellationToken)
    {
        await using RosterDb db = await OpenAsync(cancellationToken);
        return await Steps(db).Select(s => (object)new
        {
            stepId = s.Id,
            phase = s.PhaseKey,
            agent = s.AgentName,
            version = s.AgentHash.Substring(0, 12),
            model = s.EndpointName + "/" + s.Model,
            outcome = s.Outcome ?? "interrupted",
            durationMs = s.DurationMs,
            inputTokens = s.InputTokens,
            outputTokens = s.OutputTokens,
        }).ToListAsync(cancellationToken);
    }

    private async Task<object> GetStepAsync([Description("A step id from list_steps.")] string stepId, CancellationToken cancellationToken)
    {
        await using RosterDb db = await OpenAsync(cancellationToken);
        Invocation step = await FindStepAsync(db, stepId, cancellationToken);
        return new
        {
            stepId = step.Id,
            agent = step.AgentName,
            input = Shorten(step.InputText, MaxTextLength),
            output = step.OutputJson is { } json ? JsonDocument.Parse(json).RootElement : (object?)Shorten(step.RawText, MaxTextLength),
            toolCalls = step.ToolCallsJson is { } calls ? JsonDocument.Parse(calls).RootElement : (object?)null,
            step.InputTokens,
            step.OutputTokens,
            step.DurationMs,
            outcome = step.Outcome ?? "interrupted",
        };
    }

    private async Task<List<object>> GetFindingsAsync(CancellationToken cancellationToken)
    {
        await using RosterDb db = await OpenAsync(cancellationToken);
        return await db.Findings.AsNoTracking()
            .Where(f => f.AssignmentId == context.AssignmentId)
            .OrderBy(f => f.AgentName).ThenBy(f => f.Rank)
            .Select(f => (object)new
            {
                agent = f.AgentName,
                f.Title,
                f.Severity,
                f.FilePath,
                f.Confidence,
                decision = f.Decision.ToString().ToLower(),
                reason = f.DecisionReason,
                decidedBy = f.DecidedByTitle == null ? f.DecidedByName : f.DecidedByName + " · " + f.DecidedByTitle,
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<string> GetReasoningAsync([Description("A step id from list_steps.")] string stepId, CancellationToken cancellationToken)
    {
        await using RosterDb db = await OpenAsync(cancellationToken);
        Invocation step = await FindStepAsync(db, stepId, cancellationToken);
        return step.Reasoning is { Length: > 0 } reasoning
            ? Shorten(reasoning, MaxTextLength)!
            : "Not captured: the endpoint did not return reasoning for this step. Do not guess what the agent was thinking.";
    }

    private async Task<string> GetChatAsync([Description("A step id from list_steps.")] string stepId, CancellationToken cancellationToken)
    {
        await using RosterDb db = await OpenAsync(cancellationToken);
        Invocation step = await FindStepAsync(db, stepId, cancellationToken);
        return step.OutputStrategy == "text"
            ? $"{Shorten(step.InputText, MaxTextLength)}\n\nassistant: {Shorten(step.RawText, MaxTextLength)}"
            : $"Step {step.Id} ({step.AgentName}) was not a conversation; use get_step instead.";
    }

    // Opens a context and checks, once per tool call, that the person this call runs for owns the assignment.
    private async Task<RosterDb> OpenAsync(CancellationToken cancellationToken)
    {
        RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        bool owns = await db.Assignments.AnyAsync(a => a.Id == context.AssignmentId && a.OwnerId == context.UserId, cancellationToken);
        if (!owns)
        {
            await db.DisposeAsync();
            throw new UnauthorizedAccessException("This assignment is not readable for the person this conversation is with.");
        }

        return db;
    }

    private IQueryable<Invocation> Steps(RosterDb db) =>
        db.Invocations.AsNoTracking()
            .Where(i => i.AssignmentId == context.AssignmentId && i.PhaseKey != RetroPhase)
            .OrderBy(i => i.StartedAt);

    private async Task<Invocation> FindStepAsync(RosterDb db, string stepId, CancellationToken cancellationToken) =>
        Guid.TryParse(stepId, out Guid id)
        && await Steps(db).SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is { } step
            ? step
            : throw new ArgumentException($"No step '{stepId}' in this assignment. Use list_steps for the ids.");

    private static string? Shorten(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + $"\n[... {text.Length - max:N0} more characters]";
}
