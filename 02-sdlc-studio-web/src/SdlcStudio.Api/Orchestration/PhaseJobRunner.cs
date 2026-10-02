using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using SdlcStudio.Api.Ai;
using SdlcStudio.Api.Data;
using SdlcStudio.Api.Domain;
using SdlcStudio.Api.Tools;
using SdlcStudio.Api.Workflows;

namespace SdlcStudio.Api.Orchestration;

/// <summary>
/// Executes one <see cref="PhaseJob"/>: loads the run from the database, builds the agents and the phase's
/// workflow, runs it, and maps the typed workflow output back into database rows.
/// </summary>
/// <remarks>
/// This class is the seam between two worlds. Workflows know nothing about EF Core; the database knows
/// nothing about executors. WHY keep them apart? Workflows stay unit-testable with a fake agent and no
/// database, and the persistence model can change without touching the agent graph.
/// </remarks>
public sealed class PhaseJobRunner(
    StudioDbContext db,
    PromptLibrary prompts,
    AgentFactory agents,
    WorkflowRunner workflows,
    RunJournal journal,
    RunEventHub hub,
    PhaseJobQueue queue,
    JiraClient jira,
    ConfluenceClient confluence,
    GitWorkspace git,
    MergeRequestPublisher mergeRequests,
    GitHubMcpTools githubMcp)
{
    /// <summary>What a phase handler wants to happen next.</summary>
    private sealed record Outcome(RunStatus Status, string Message, JobKind? FollowUp = null, WorkflowPhase? NextPhase = null);

    public async Task ExecuteAsync(PhaseJob job, CancellationToken ct)
    {
        var run = await db.Runs.SingleAsync(r => r.Id == job.RunId, ct);
        var context = new RunContext(run.Id, run.Phase, run.Name, run.WorkspacePath, await prompts.LoadPromptsAsync(ct));
        await journal.WriteAsync(run.Id, run.Phase, $"Started job {job.Kind}", "job", ct);

        Outcome outcome;
        try
        {
            outcome = job.Kind switch
            {
                JobKind.InterviewTurn => await InterviewTurnAsync(run, context, ct),
                JobKind.Design => await DesignAsync(run, context, ct),
                JobKind.Import => await ImportAsync(run, context, ct),
                JobKind.Review => await ReviewAsync(run, context, ct),
                JobKind.Revise => await ReviseAsync(run, context, ct),
                JobKind.AgilePlan => await AgilePlanAsync(run, context, ct),
                JobKind.DevPlan => await DevPlanAsync(run, context, ct),
                JobKind.DevelopStory => await DevelopStoryAsync(run, context, ct),
                JobKind.Testing => await TestingAsync(run, context, ct),
                _ => throw new NotSupportedException(job.Kind.ToString()),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear(); // discard half-applied changes from the failed handler
            run = await db.Runs.SingleAsync(r => r.Id == job.RunId, ct);
            outcome = new Outcome(RunStatus.Failed, $"{job.Kind} failed: {ex.GetBaseException().Message}");
            await journal.WriteAsync(run.Id, run.Phase, outcome.Message, "error", ct);
        }

        run.Phase = outcome.NextPhase ?? run.Phase;
        run.Status = outcome.FollowUp is null ? outcome.Status : RunStatus.Running;
        run.StatusMessage = outcome.Message;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        if (outcome.Status != RunStatus.Failed)
        {
            // Keep PendingJob on failure so "Retry" knows what to run again.
            run.PendingJob = outcome.FollowUp?.ToString();
            run.PendingFeedback = null;
        }
        await db.SaveChangesAsync(ct);

        hub.Publish(new RunNotification(run.Id, "status", outcome.Message));
        if (outcome.FollowUp is { } next)
            await queue.EnqueueAsync(new PhaseJob(run.Id, next), ct);
    }

    // ----- Requirements: one interviewer turn ----------------------------------------------------

    private async Task<Outcome> InterviewTurnAsync(WorkflowRun run, RunContext context, CancellationToken ct)
    {
        // Not a workflow: the interview is ONE agent having a long conversation with a human across many
        // HTTP requests. The agent's AgentSession (its chat history) is serialized into the run row after
        // every turn and restored on the next one.
        // ALTERNATIVE: a workflow with a RequestPort that emits a RequestInfoEvent for each question and
        // resumes on ExternalResponse, combined with checkpointing to survive the gap between turns.
        var interviewer = await agents.CreateAsync("interviewer", ct: ct);

        AgentSession session;
        string message;
        if (run.InterviewSessionJson is null)
        {
            session = await interviewer.CreateSessionAsync(ct);
            message = context.Prompt("interview-start", ("idea", run.Idea));
        }
        else
        {
            using var json = JsonDocument.Parse(run.InterviewSessionJson);
            session = await interviewer.DeserializeSessionAsync(json.RootElement, cancellationToken: ct);
            message = await db.Messages.Where(m => m.RunId == run.Id && m.Role == "user")
                .OrderByDescending(m => m.Id).Select(m => m.Content).FirstAsync(ct);
        }

        var response = await interviewer.RunAsync(message, session, cancellationToken: ct);
        run.InterviewSessionJson = (await interviewer.SerializeSessionAsync(session, cancellationToken: ct)).GetRawText();

        const string Marker = "REQUIREMENTS COMPLETE";
        var text = response.Text.Trim();
        db.Messages.Add(new RunMessage { RunId = run.Id, Role = "assistant", Content = text.Replace(Marker, "").Trim() });

        if (!text.Contains(Marker, StringComparison.OrdinalIgnoreCase))
            return new(RunStatus.AwaitingInput, "The interviewer has a question for you");

        var summary = text[(text.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) + Marker.Length)..].Trim();
        await AddArtifactAsync(run, WorkflowPhase.Requirements, "requirements", "Agreed requirements", summary, "specs/requirements.md", ct);
        return new(RunStatus.AwaitingApproval, "Requirements captured. Approve to generate the specs, or reject with what is missing.");
    }

    // ----- Design / Import / Revise --------------------------------------------------------------

    private async Task<Outcome> DesignAsync(WorkflowRun run, RunContext context, CancellationToken ct)
    {
        var requirements = await LatestArtifactAsync(run.Id, "requirements", ct) ?? run.Idea ?? "";
        var workflow = SpecWorkflows.BuildDesign(context,
            await agents.CreateAsync("spec-writer", ct: ct),
            await agents.CreateAsync("architect", ct: ct));

        var specs = await workflows.RunAsync<DesignRequest, SpecBundle>(workflow, new DesignRequest(requirements), context, ct);
        await SaveSpecsAsync(run, WorkflowPhase.Design, specs, ct);
        return new(RunStatus.Running, "Specs drafted; starting the review", JobKind.Review, WorkflowPhase.Review);
    }

    private async Task<Outcome> ImportAsync(WorkflowRun run, RunContext context, CancellationToken ct)
    {
        var source = run.SourcePath ?? throw new InvalidOperationException("No source path for import.");
        var codebase = new CodebaseTools(source);

        // The analyst gets the file tools; the framework handles the tool-calling loop.
        var analyst = await agents.CreateAsync("app-analyst", codebase.AsTools(), ct);
        var workflow = SpecWorkflows.BuildImport(context, analyst, await agents.CreateAsync("architect", ct: ct), codebase, githubMcp);

        var specs = await workflows.RunAsync<ImportRequest, SpecBundle>(workflow, new ImportRequest(source, run.Name), context, ct);
        await SaveSpecsAsync(run, WorkflowPhase.Import, specs, ct);
        return new(RunStatus.Running, "Specs generated from the existing app; starting the review", JobKind.Review, WorkflowPhase.Review);
    }

    private async Task<Outcome> ReviseAsync(WorkflowRun run, RunContext context, CancellationToken ct)
    {
        var findings = await db.Findings.Where(f => f.RunId == run.Id && f.Round == run.ReviewRound).ToListAsync(ct);
        static string Format(IEnumerable<ReviewFinding> fs) =>
            string.Join("\n", fs.Select(f => $"- {f.Title}: {f.Detail} (reason: {f.DecisionReason ?? "none given"})"));

        var accepted = Format(findings.Where(f => f.Status == FindingStatus.Accepted));
        if (!string.IsNullOrWhiteSpace(run.PendingFeedback))
            accepted += $"\n- Reviewer feedback: {run.PendingFeedback} (reason: requested by the human reviewer)";

        var workflow = SpecWorkflows.BuildRevise(context, await agents.CreateAsync("spec-reviser", ct: ct));
        var specs = await workflows.RunAsync<ReviseRequest, SpecBundle>(workflow,
            new ReviseRequest(await CurrentSpecsAsync(run.Id, ct), accepted, Format(findings.Where(f => f.Status == FindingStatus.Rejected))),
            context, ct);

        await SaveSpecsAsync(run, WorkflowPhase.Review, specs, ct);
        return new(RunStatus.Running, "Spec revised; starting the next review round", JobKind.Review);
    }

    // ----- Review --------------------------------------------------------------------------------

    private async Task<Outcome> ReviewAsync(WorkflowRun run, RunContext context, CancellationToken ct)
    {
        string[] lenses = ["reviewer-product", "reviewer-architecture", "reviewer-qa"];
        List<AIAgent> reviewers = [];
        foreach (var lens in lenses) reviewers.Add(await agents.CreateAsync(lens, ct: ct));

        run.ReviewRound++;
        var report = await workflows.RunAsync<ReviewRequest, ReviewReport>(
            ReviewWorkflow.Build(context, reviewers), new ReviewRequest(run.ReviewRound, await CurrentSpecsAsync(run.Id, ct)), context, ct);

        foreach (var lens in report.Lenses)
            foreach (var f in lens.Findings)
                db.Findings.Add(new ReviewFinding
                {
                    RunId = run.Id, Round = run.ReviewRound, Reviewer = lens.Reviewer,
                    Severity = f.Severity, Title = f.Title, Detail = f.Detail,
                });

        var count = report.Lenses.Sum(l => l.Findings.Count);
        return new(RunStatus.AwaitingApproval, count == 0
            ? $"Review round {run.ReviewRound}: no findings. Approve the specs to start planning."
            : $"Review round {run.ReviewRound}: {count} finding(s). Accept or reject each with a reason, then revise or approve.");
    }

    // ----- Planning ------------------------------------------------------------------------------

    private async Task<Outcome> AgilePlanAsync(WorkflowRun run, RunContext context, CancellationToken ct)
    {
        var specs = await CurrentSpecsAsync(run.Id, ct);
        var workflow = AgilePlanWorkflow.Build(context, await agents.CreateAsync("agile-planner", ct: ct), jira);
        var result = await workflows.RunAsync<PlanRequest, PublishedPlan>(workflow, new PlanRequest(specs.FunctionalSpec, run.PendingFeedback), context, ct);

        // Re-planning replaces the backlog. (Tasks reference stories, so remove them first.)
        await db.Tasks.Where(t => t.RunId == run.Id).ExecuteDeleteAsync(ct);
        await db.Stories.Where(s => s.RunId == run.Id).ExecuteDeleteAsync(ct);
        await db.Epics.Where(e => e.RunId == run.Id).ExecuteDeleteAsync(ct);

        foreach (var e in result.Plan.Epics)
        {
            var epic = new Epic { RunId = run.Id, Key = e.Key, Title = e.Title, Description = e.Description, ExternalId = result.JiraKeys.GetValueOrDefault(e.Key) };
            db.Epics.Add(epic);
            await db.SaveChangesAsync(ct); // need the generated epic id for its stories
            db.Stories.AddRange(e.Stories.Select(s => new Story
            {
                RunId = run.Id, EpicId = epic.Id, Key = s.Key, Title = s.Title, UserStory = s.UserStory,
                AcceptanceCriteria = string.Join("\n", s.AcceptanceCriteria), Points = s.Points, Sprint = s.Sprint,
                ExternalId = result.JiraKeys.GetValueOrDefault(s.Key),
            }));
        }
        await AddArtifactAsync(run, WorkflowPhase.AgilePlan, "backlog", "Backlog (epics, sprints, stories)", result.Markdown, "plan/backlog.md", ct);

        var warning = result.Warnings.Count > 0 ? $" Warning: {result.Warnings.Count} validation issue(s) remain." : "";
        return new(RunStatus.AwaitingApproval, $"Plan ready: {result.Plan.Epics.Count} epics, {result.Plan.Epics.Sum(e => e.Stories.Count)} stories.{warning} Approve to break stories into tasks.");
    }

    private async Task<Outcome> DevPlanAsync(WorkflowRun run, RunContext context, CancellationToken ct)
    {
        var stories = await StoriesAsync(run.Id, ct);
        var specs = await CurrentSpecsAsync(run.Id, ct);
        var workflow = DevPlanWorkflow.Build(context, await agents.CreateAsync("dev-planner", ct: ct));
        var result = await workflows.RunAsync<DevPlanRequest, DevPlanResult>(workflow,
            new DevPlanRequest([.. stories.Select(ToInput)], specs.TechnicalDesign, run.PendingFeedback), context, ct);

        await db.Tasks.Where(t => t.RunId == run.Id).ExecuteDeleteAsync(ct);
        foreach (var (story, tasks) in result.Stories)
            db.Tasks.AddRange(tasks.Select((t, i) => new DevTask
            {
                RunId = run.Id, StoryId = story.StoryId, Order = i + 1, Title = t.Title, Detail = t.Detail, Files = string.Join(", ", t.Files),
            }));
        await AddArtifactAsync(run, WorkflowPhase.DevPlan, "tasks", "Development tasks", result.Markdown, "plan/tasks.md", ct);

        return new(RunStatus.AwaitingApproval, $"{result.Stories.Sum(s => s.Tasks.Count)} tasks planned. Approve to start developing story by story.");
    }

    // ----- Develop -------------------------------------------------------------------------------

    private async Task<Outcome> DevelopStoryAsync(WorkflowRun run, RunContext context, CancellationToken ct)
    {
        var story = await db.Stories.Include(s => s.Tasks)
            .Where(s => s.RunId == run.Id && s.Status != WorkItemStatus.Done)
            .OrderBy(s => s.Sprint).ThenBy(s => s.Id)
            .FirstOrDefaultAsync(ct);
        if (story is null)
            return new(RunStatus.Running, "All stories delivered; generating tester prompts", JobKind.Testing, WorkflowPhase.Testing);

        story.Status = WorkItemStatus.InProgress;
        foreach (var t in story.Tasks) t.Status = WorkItemStatus.InProgress;
        await db.SaveChangesAsync(ct);

        // Map story keys to Jira keys so the developer's update_jira_ticket tool comments on the right issue.
        var jiraKeys = await db.Stories.Where(s => s.RunId == run.Id && s.ExternalId != null)
            .ToDictionaryAsync(s => s.Key, s => s.ExternalId!, ct);
        var jiraTools = new JiraTools(jira, jiraKeys, msg => journal.WriteAsync(run.Id, run.Phase, msg, "tool", ct));

        var developer = await agents.CreateAsync("developer", jiraTools.AsTools(), ct);
        var reviewer = await agents.CreateAsync("code-reviewer", ct: ct);
        var workflow = DevelopWorkflow.Build(context, developer, reviewer, git, mergeRequests);

        var work = new StoryWork(
            ToInput(story),
            [.. story.Tasks.OrderBy(t => t.Order).Select(t => new TaskInput(t.Id, t.Order, t.Title, t.Detail, t.Files))],
            Branch: $"feature/{story.Key.ToLowerInvariant()}",
            run.PendingFeedback);
        if (work.Tasks.Count == 0)
            throw new InvalidOperationException($"{story.Key} has no tasks. Re-run the dev plan.");

        var delivered = await workflows.RunAsync<StoryWork, StoryDelivered>(workflow, work, context, ct);

        foreach (var commit in delivered.Commits)
        {
            var task = story.Tasks.Single(t => t.Id == commit.Task.TaskId);
            task.Status = WorkItemStatus.Done;
            task.CommitSha = commit.Sha;
            db.Checkpoints.Add(new Checkpoint
            {
                RunId = run.Id, StoryId = story.Id, Kind = CheckpointKind.Commit, Branch = delivered.Branch, Sha = commit.Sha,
                Title = commit.Message, Detail = commit.ApprovedByReviewer ? "Approved by code-reviewer agent" : "Committed after max review attempts",
            });
        }
        db.Checkpoints.Add(new Checkpoint
        {
            RunId = run.Id, StoryId = story.Id, Kind = CheckpointKind.MergeRequest, Branch = delivered.Branch,
            Title = $"MR: {story.Key} {story.Title}", Detail = delivered.MergeRequest,
        });

        return new(RunStatus.AwaitingApproval, $"Merge request checkpoint for {story.Key} is ready. Approve to merge and continue, or reject with feedback.");
    }

    // ----- Testing -------------------------------------------------------------------------------

    private async Task<Outcome> TestingAsync(WorkflowRun run, RunContext context, CancellationToken ct)
    {
        var stories = await StoriesAsync(run.Id, ct);
        var workflow = TestingWorkflow.Build(context, await agents.CreateAsync("test-designer", ct: ct), confluence);
        var result = await workflows.RunAsync<TestingRequest, TestingResult>(workflow,
            new TestingRequest([.. stories.Select(ToInput)], run.PendingFeedback), context, ct);

        await AddArtifactAsync(run, WorkflowPhase.Testing, "test-prompts", "Tester prompts", result.Markdown, "test-prompts/", ct);
        return new(RunStatus.AwaitingApproval, $"{result.Prompts.Prompts.Count} tester prompt(s) ready (published to {result.ConfluenceUrl}). Approve to finish.");
    }

    // ----- helpers -------------------------------------------------------------------------------

    private static StoryInput ToInput(Story s) => new(s.Id, s.Key, s.Title, s.UserStory, s.AcceptanceCriteria);

    private Task<List<Story>> StoriesAsync(Guid runId, CancellationToken ct) =>
        db.Stories.Where(s => s.RunId == runId).OrderBy(s => s.Sprint).ThenBy(s => s.Id).ToListAsync(ct);

    private Task<string?> LatestArtifactAsync(Guid runId, string kind, CancellationToken ct) =>
        db.Artifacts.Where(a => a.RunId == runId && a.Kind == kind).OrderByDescending(a => a.Version).Select(a => a.Content).FirstOrDefaultAsync(ct);

    private async Task<SpecBundle> CurrentSpecsAsync(Guid runId, CancellationToken ct) => new(
        await LatestArtifactAsync(runId, "functional-spec", ct) ?? throw new InvalidOperationException("No functional spec yet."),
        await LatestArtifactAsync(runId, "technical-design", ct) ?? "");

    private async Task SaveSpecsAsync(WorkflowRun run, WorkflowPhase phase, SpecBundle specs, CancellationToken ct)
    {
        await AddArtifactAsync(run, phase, "functional-spec", "Functional specification", specs.FunctionalSpec, "specs/functional-spec.md", ct);
        await AddArtifactAsync(run, phase, "technical-design", "Technical design", specs.TechnicalDesign, "specs/technical-design.md", ct);
    }

    private async Task AddArtifactAsync(WorkflowRun run, WorkflowPhase phase, string kind, string title, string content, string path, CancellationToken ct)
    {
        var version = await db.Artifacts.Where(a => a.RunId == run.Id && a.Kind == kind).MaxAsync(a => (int?)a.Version, ct) ?? 0;
        db.Artifacts.Add(new Artifact { RunId = run.Id, Phase = phase, Kind = kind, Title = title, Content = content, RelativePath = path, Version = version + 1 });

        if (kind == "requirements")
        {
            // The other artifacts are written to disk by workflow publisher steps; requirements come from the interview.
            var full = Path.Combine(run.WorkspacePath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, ct);
        }
    }
}
