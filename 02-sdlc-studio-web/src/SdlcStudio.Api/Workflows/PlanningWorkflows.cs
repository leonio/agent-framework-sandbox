using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using SdlcStudio.Api.Domain;
using SdlcStudio.Api.Tools;

namespace SdlcStudio.Api.Workflows;

// =============================================================================================
// Agile plan workflow: a generate -> validate loop with conditional edges.
//
//   PlanRequest --> [agile-planner] --> [validate plan] --(valid or out of attempts)--> [publish plan]
//                         ^                    |
//                         +--[retry adapter]<--+--(issues found)
//
// The validator is plain C#: deterministic rules (story size, acceptance criteria, sprint numbers).
// WHY not ask another LLM to validate? Rules that can be checked in code should be: it is free,
// instant and never hallucinates. Use an LLM "critic" for the fuzzy things (is this story INVEST?).
//
// Dev plan workflow:
//   DevPlanRequest --> [dev-planner, once per story] --> [publish tasks]
// =============================================================================================

public sealed record PlanRequest(string Spec, string? Feedback, int Attempt = 1);
public sealed record PlanValidation(PlanRequest Request, AgilePlan Plan, List<string> Issues);
public sealed record PublishedPlan(AgilePlan Plan, Dictionary<string, string> JiraKeys, List<string> Warnings, string Markdown);

public static class AgilePlanWorkflow
{
    public const int MaxAttempts = 3;

    public static Workflow Build(RunContext run, AIAgent planner, JiraClient jira)
    {
        var plan = new AgilePlannerExecutor(run, planner);
        var validate = new PlanValidatorExecutor();
        var publish = new PlanPublisherExecutor(run, jira);

        // A function executor: a lambda becomes a workflow step. Perfect for small adapters like
        // "turn validation issues into feedback for the next attempt". No class needed.
        var retry = ExecutorBindingExtensions.BindAsExecutor<PlanValidation, PlanRequest>(
            v => v.Request with
            {
                Attempt = v.Request.Attempt + 1,
                Feedback = $"{v.Request.Feedback}\n\nValidation issues in your previous plan (fix all of them):\n- {string.Join("\n- ", v.Issues)}",
            },
            "retry-with-feedback");

        return new WorkflowBuilder(plan)
            .WithName("agile-plan")
            .AddEdge(plan, validate)
            // Conditional edges: exactly one of these fires for each validation result.
            .AddEdge<PlanValidation>(validate, retry, v => v!.Issues.Count > 0 && v.Request.Attempt < MaxAttempts)
            .AddEdge<PlanValidation>(validate, publish, v => v!.Issues.Count == 0 || v.Request.Attempt >= MaxAttempts)
            .AddEdge(retry, plan)
            .WithOutputFrom(publish)
            .Build();
    }
}

internal sealed class AgilePlannerExecutor(RunContext run, AIAgent agent) : Executor<PlanRequest, PlanValidation>("agile-planner")
{
    public override async ValueTask<PlanValidation> HandleAsync(PlanRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        if (message.Attempt > 1)
            await context.AddEventAsync(new StudioProgressEvent($"Re-planning (attempt {message.Attempt}) with validator feedback"), ct);

        var prompt = run.Prompt("agile-plan", ("spec", message.Spec), ("feedback", message.Feedback));
        var response = await agent.RunAsync<AgilePlan>(prompt, serializerOptions: StudioJson.Options, cancellationToken: ct);
        return new PlanValidation(message, response.Result, []);
    }
}

/// <summary>Deterministic rules. Returns the same message with its Issues filled in.</summary>
internal sealed class PlanValidatorExecutor() : Executor<PlanValidation, PlanValidation>("validate-plan")
{
    private static readonly int[] Fibonacci = [1, 2, 3, 5, 8];

    public override async ValueTask<PlanValidation> HandleAsync(PlanValidation message, IWorkflowContext context, CancellationToken ct = default)
    {
        var sprints = message.Plan.Sprints.Select(s => s.Number).ToHashSet();
        var stories = message.Plan.Epics.SelectMany(e => e.Stories).ToList();

        List<string> issues =
        [
            .. stories.Where(s => !Fibonacci.Contains(s.Points)).Select(s => $"{s.Key} has {s.Points} points; split it so every story is 1, 2, 3, 5 or 8."),
            .. stories.Where(s => s.AcceptanceCriteria.Count < 2).Select(s => $"{s.Key} needs at least two acceptance criteria."),
            .. stories.Where(s => !sprints.Contains(s.Sprint)).Select(s => $"{s.Key} is assigned to sprint {s.Sprint}, which is not in the plan."),
            .. stories.GroupBy(s => s.Key).Where(g => g.Count() > 1).Select(g => $"Story key {g.Key} is used more than once."),
        ];
        if (stories.Count == 0) issues.Add("The plan has no stories.");

        await context.AddEventAsync(new StudioProgressEvent(issues.Count == 0
            ? $"Plan validated: {message.Plan.Epics.Count} epics, {stories.Count} stories"
            : $"Plan validation found {issues.Count} issue(s): {string.Join(" ", issues)}"), ct);

        return message with { Issues = issues };
    }
}

/// <summary>Writes plan/backlog.md and creates the Jira epics and stories (deterministic tool use).</summary>
[YieldsOutput(typeof(PublishedPlan))]
internal sealed class PlanPublisherExecutor(RunContext run, JiraClient jira) : Executor<PlanValidation>("publish-plan")
{
    public override async ValueTask HandleAsync(PlanValidation message, IWorkflowContext context, CancellationToken ct = default)
    {
        var keys = new Dictionary<string, string>();
        foreach (var epic in message.Plan.Epics)
        {
            keys[epic.Key] = await jira.CreateIssueAsync($"{epic.Key} {epic.Title}", epic.Description, "Epic", ct);
            foreach (var story in epic.Stories)
                keys[story.Key] = await jira.CreateIssueAsync($"{story.Key} {story.Title}",
                    $"{story.UserStory}\n\n{string.Join("\n", story.AcceptanceCriteria)}", "Story", ct);
        }
        await context.AddEventAsync(new StudioProgressEvent(
            $"Created {keys.Count} Jira issues{(jira.IsMock ? " (mock)" : "")}: {string.Join(", ", keys.Values.Take(6))}..."), ct);

        var md = ToMarkdown(run.Project, message.Plan, keys);
        Directory.CreateDirectory(run.PlanFolder);
        await File.WriteAllTextAsync(Path.Combine(run.PlanFolder, "backlog.md"), md, ct);

        // Out of attempts but still invalid: publish anyway and surface the warnings to the human gate.
        await context.YieldOutputAsync(new PublishedPlan(message.Plan, keys, message.Issues, md), ct);
    }

    internal static string ToMarkdown(string project, AgilePlan plan, IReadOnlyDictionary<string, string> keys)
    {
        var sb = new StringBuilder($"# {project}: backlog\n\n## Sprints\n");
        foreach (var s in plan.Sprints.OrderBy(s => s.Number)) sb.AppendLine($"- **Sprint {s.Number}**: {s.Goal}");
        foreach (var e in plan.Epics)
        {
            sb.AppendLine($"\n## {e.Key} {e.Title} ({keys.GetValueOrDefault(e.Key)})\n{e.Description}\n");
            foreach (var st in e.Stories)
            {
                sb.AppendLine($"### {st.Key} {st.Title} [{st.Points} pts, sprint {st.Sprint}] ({keys.GetValueOrDefault(st.Key)})");
                sb.AppendLine(st.UserStory);
                foreach (var ac in st.AcceptanceCriteria) sb.AppendLine($"- [ ] {ac}");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}

// ----- Dev plan --------------------------------------------------------------------------------

public sealed record DevPlanRequest(List<StoryInput> Stories, string Design, string? Feedback);
public sealed record StoryTasks(StoryInput Story, List<DevTaskDraft> Tasks);
public sealed record DevPlanResult(List<StoryTasks> Stories, string Markdown);

public static class DevPlanWorkflow
{
    public static Workflow Build(RunContext run, AIAgent devPlanner)
    {
        var breakdown = new TaskBreakdownExecutor(run, devPlanner);
        var publish = new DevPlanPublisherExecutor(run);
        return new WorkflowBuilder(breakdown).WithName("dev-plan").AddEdge(breakdown, publish).WithOutputFrom(publish).Build();
    }
}

/// <summary>
/// Calls the dev-planner agent once per story.
/// </summary>
/// <remarks>
/// WHY a loop inside one executor rather than a fan-out edge per story? Edges are static (declared at
/// build time) but the number of stories is only known at run time. ALTERNATIVES: build the graph
/// dynamically with one executor per story, or run the stories concurrently with Task.WhenAll using
/// one agent instance per story (a single agent instance should not run concurrent calls).
/// </remarks>
internal sealed class TaskBreakdownExecutor(RunContext run, AIAgent agent) : Executor<DevPlanRequest, List<StoryTasks>>("dev-planner")
{
    public override async ValueTask<List<StoryTasks>> HandleAsync(DevPlanRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        List<StoryTasks> result = [];
        foreach (var story in message.Stories)
        {
            var prompt = run.Prompt("dev-plan", ("story", story.ToMarkdown()), ("design", message.Design), ("feedback", message.Feedback));
            var response = await agent.RunAsync<TaskBreakdown>(prompt, serializerOptions: StudioJson.Options, cancellationToken: ct);
            result.Add(new StoryTasks(story, response.Result.Tasks));
            await context.AddEventAsync(new StudioProgressEvent($"{story.Key}: {response.Result.Tasks.Count} task(s)"), ct);
        }
        return result;
    }
}

[YieldsOutput(typeof(DevPlanResult))]
internal sealed class DevPlanPublisherExecutor(RunContext run) : Executor<List<StoryTasks>>("publish-tasks")
{
    public override async ValueTask HandleAsync(List<StoryTasks> message, IWorkflowContext context, CancellationToken ct = default)
    {
        var sb = new StringBuilder($"# {run.Project}: development tasks\n");
        foreach (var (story, tasks) in message)
        {
            sb.AppendLine($"\n## {story.Key} {story.Title}");
            foreach (var (task, i) in tasks.Select((t, i) => (t, i + 1)))
                sb.AppendLine($"{i}. **{task.Title}**: {task.Detail} _(files: {string.Join(", ", task.Files)})_");
        }

        Directory.CreateDirectory(run.PlanFolder);
        await File.WriteAllTextAsync(Path.Combine(run.PlanFolder, "tasks.md"), sb.ToString(), ct);
        await context.YieldOutputAsync(new DevPlanResult(message, sb.ToString()), ct);
    }
}
