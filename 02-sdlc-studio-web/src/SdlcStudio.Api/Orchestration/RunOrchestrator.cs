using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SdlcStudio.Api.Data;
using SdlcStudio.Api.Domain;
using SdlcStudio.Api.Options;
using SdlcStudio.Api.Tools;

namespace SdlcStudio.Api.Orchestration;

/// <summary>
/// The human side of the workflow: every command a user can issue (start, answer, approve, reject,
/// decide on a finding, revise, retry). It validates the command against the run's current state,
/// records the decision, and queues the next phase job.
/// </summary>
/// <remarks>
/// This is a small explicit state machine. WHY not let an LLM decide what happens next ("magentic"
/// orchestration)? The SDLC has well-known, auditable transitions, and humans must be able to predict
/// what "Approve" does. Use model-driven orchestration where the path genuinely is not known up front.
/// </remarks>
public sealed partial class RunOrchestrator(
    StudioDbContext db,
    PhaseJobQueue queue,
    RunEventHub hub,
    RunJournal journal,
    GitWorkspace git,
    IOptions<StudioOptions> options,
    IHostEnvironment env)
{
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonSlug();

    public async Task<WorkflowRun> StartNewIdeaAsync(string name, string idea, CancellationToken ct)
    {
        var run = NewRun(name, RunSource.NewIdea, WorkflowPhase.Requirements);
        run.Idea = idea;
        run.Messages.Add(new RunMessage { Role = "user", Content = idea });
        db.Runs.Add(run);
        return await QueueAsync(run, JobKind.InterviewTurn, "Interviewer is preparing the first question", ct);
    }

    /// <summary>Import from a folder the API can read (a local path or a share).</summary>
    public async Task<WorkflowRun> StartImportFromPathAsync(string name, string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
            throw new StudioValidationException($"Folder not found on the API host: {path}");

        // NOTE: the app has no auth (by design for this sample), so this endpoint can read any folder the
        // API process can. Never expose it beyond localhost without authentication and an allow-list.
        var run = NewRun(name, RunSource.ExistingApp, WorkflowPhase.Import);
        run.SourcePath = Path.GetFullPath(path);
        db.Runs.Add(run);
        return await QueueAsync(run, JobKind.Import, "Analysing the existing app", ct);
    }

    /// <summary>Import from an uploaded .zip, extracted into the run's workspace.</summary>
    public async Task<WorkflowRun> StartImportFromZipAsync(string name, Stream zip, CancellationToken ct)
    {
        var run = NewRun(name, RunSource.ExistingApp, WorkflowPhase.Import);
        var target = Path.Combine(run.WorkspacePath, "source");
        Directory.CreateDirectory(target);

        // ExtractToDirectory rejects entries that would escape the target folder ("zip slip").
        await ZipFile.ExtractToDirectoryAsync(zip, target, overwriteFiles: true, ct);
        run.SourcePath = target;
        db.Runs.Add(run);
        return await QueueAsync(run, JobKind.Import, "Analysing the uploaded app", ct);
    }

    public async Task<WorkflowRun> AnswerAsync(Guid runId, string answer, CancellationToken ct)
    {
        var run = await LoadAsync(runId, ct);
        Require(run.Phase == WorkflowPhase.Requirements && run.Status is RunStatus.AwaitingInput or RunStatus.AwaitingApproval,
            "The interviewer is not waiting for an answer.");

        db.Messages.Add(new RunMessage { RunId = run.Id, Role = "user", Content = answer });
        return await QueueAsync(run, JobKind.InterviewTurn, "Interviewer is thinking", ct);
    }

    public async Task<WorkflowRun> ApproveAsync(Guid runId, string? reason, CancellationToken ct)
    {
        var run = await LoadAsync(runId, ct);
        Require(run.Status == RunStatus.AwaitingApproval, "Nothing is waiting for approval.");
        RecordDecision(run, approved: true, reason);

        return run.Phase switch
        {
            WorkflowPhase.Requirements => await QueueAsync(run, JobKind.Design, "Writing the specs", ct, WorkflowPhase.Design),
            WorkflowPhase.Review => await QueueAsync(run, JobKind.AgilePlan, "Planning epics, sprints and stories", ct, WorkflowPhase.AgilePlan),
            WorkflowPhase.AgilePlan => await QueueAsync(run, JobKind.DevPlan, "Breaking stories into tasks", ct, WorkflowPhase.DevPlan),
            WorkflowPhase.DevPlan => await QueueAsync(run, JobKind.DevelopStory, "Developing the first story", ct, WorkflowPhase.Develop),
            WorkflowPhase.Develop => await ApproveMergeRequestAsync(run, ct),
            WorkflowPhase.Testing => await CompleteAsync(run, ct),
            _ => throw new StudioValidationException($"Phase {run.Phase} has no approval step."),
        };
    }

    public async Task<WorkflowRun> RejectAsync(Guid runId, string reason, CancellationToken ct)
    {
        var run = await LoadAsync(runId, ct);
        Require(run.Status == RunStatus.AwaitingApproval, "Nothing is waiting for approval.");
        Require(!string.IsNullOrWhiteSpace(reason), "Please say why you are rejecting it; the agents use your reason as feedback.");
        RecordDecision(run, approved: false, reason);
        run.PendingFeedback = reason;

        switch (run.Phase)
        {
            case WorkflowPhase.Requirements:
                // The reason becomes the user's next message to the interviewer.
                db.Messages.Add(new RunMessage { RunId = run.Id, Role = "user", Content = $"Not complete yet: {reason}" });
                return await QueueAsync(run, JobKind.InterviewTurn, "Interviewer is following up", ct);
            case WorkflowPhase.Review:
                return await QueueAsync(run, JobKind.Revise, "Revising the spec with your feedback", ct);
            case WorkflowPhase.AgilePlan:
                return await QueueAsync(run, JobKind.AgilePlan, "Re-planning with your feedback", ct);
            case WorkflowPhase.DevPlan:
                return await QueueAsync(run, JobKind.DevPlan, "Re-planning tasks with your feedback", ct);
            case WorkflowPhase.Develop:
                await ResetCurrentStoryAsync(run, ct);
                return await QueueAsync(run, JobKind.DevelopStory, "Re-developing the story with your feedback", ct);
            case WorkflowPhase.Testing:
                return await QueueAsync(run, JobKind.Testing, "Regenerating tester prompts with your feedback", ct);
            default:
                throw new StudioValidationException($"Phase {run.Phase} cannot be rejected.");
        }
    }

    /// <summary>Accept or reject one review finding, with the human's reason (stored and fed to the reviser).</summary>
    public async Task<ReviewFinding> DecideFindingAsync(Guid runId, long findingId, bool accepted, string? reason, CancellationToken ct)
    {
        var finding = await db.Findings.SingleOrDefaultAsync(f => f.RunId == runId && f.Id == findingId, ct)
            ?? throw new StudioNotFoundException("Finding not found.");
        finding.Status = accepted ? FindingStatus.Accepted : FindingStatus.Rejected;
        finding.DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await db.SaveChangesAsync(ct);
        hub.Publish(new RunNotification(runId, "finding", finding.Title));
        return finding;
    }

    public async Task<WorkflowRun> ReviseSpecAsync(Guid runId, CancellationToken ct)
    {
        var run = await LoadAsync(runId, ct);
        Require(run.Phase == WorkflowPhase.Review && run.Status == RunStatus.AwaitingApproval, "The spec is not under review.");
        var pending = await db.Findings.CountAsync(f => f.RunId == runId && f.Round == run.ReviewRound && f.Status == FindingStatus.Pending, ct);
        Require(pending == 0, $"Decide on the remaining {pending} finding(s) first.");
        return await QueueAsync(run, JobKind.Revise, "Revising the spec with the accepted findings", ct);
    }

    public async Task<WorkflowRun> RetryAsync(Guid runId, CancellationToken ct)
    {
        var run = await LoadAsync(runId, ct);
        Require(run.Status == RunStatus.Failed && run.PendingJob is not null, "Only failed runs can be retried.");
        return await QueueAsync(run, Enum.Parse<JobKind>(run.PendingJob!), "Retrying", ct);
    }

    public async Task DeleteAsync(Guid runId, CancellationToken ct)
    {
        var run = await LoadAsync(runId, ct);
        Require(run.Status != RunStatus.Running, "Wait for the running phase to finish before deleting.");
        db.Runs.Remove(run);
        await db.SaveChangesAsync(ct);
        hub.Publish(new RunNotification(runId, "deleted", run.Name));
    }

    // ----- transitions -----------------------------------------------------------------------------

    private async Task<WorkflowRun> ApproveMergeRequestAsync(WorkflowRun run, CancellationToken ct)
    {
        var story = await db.Stories.Where(s => s.RunId == run.Id && s.Status == WorkItemStatus.InProgress).OrderBy(s => s.Sprint).ThenBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (story is not null)
        {
            // Approving the checkpoint "merges the MR": merge the story branch into main locally.
            await git.MergeAsync(Path.Combine(run.WorkspacePath, "repo"), $"feature/{story.Key.ToLowerInvariant()}", ct);
            story.Status = WorkItemStatus.Done;
            await journal.WriteAsync(run.Id, run.Phase, $"Merged {story.Key} into {GitWorkspace.MainBranch}", "info", ct);
        }

        var remaining = await db.Stories.CountAsync(s => s.RunId == run.Id && s.Status != WorkItemStatus.Done && s.Id != (story == null ? 0 : story.Id), ct);
        return remaining > 0
            ? await QueueAsync(run, JobKind.DevelopStory, "Developing the next story", ct)
            : await QueueAsync(run, JobKind.Testing, "All stories merged; generating tester prompts", ct, WorkflowPhase.Testing);
    }

    private async Task<WorkflowRun> CompleteAsync(WorkflowRun run, CancellationToken ct)
    {
        run.Phase = WorkflowPhase.Done;
        run.Status = RunStatus.Completed;
        run.StatusMessage = "Workflow complete";
        run.PendingJob = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        hub.Publish(new RunNotification(run.Id, "status", run.StatusMessage));
        return run;
    }

    private async Task ResetCurrentStoryAsync(WorkflowRun run, CancellationToken ct)
    {
        var story = await db.Stories.Include(s => s.Tasks).FirstOrDefaultAsync(s => s.RunId == run.Id && s.Status == WorkItemStatus.InProgress, ct);
        if (story is null) return;
        foreach (var t in story.Tasks) { t.Status = WorkItemStatus.Todo; t.CommitSha = null; }
    }

    // ----- helpers ---------------------------------------------------------------------------------

    private WorkflowRun NewRun(string name, RunSource source, WorkflowPhase phase)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new StudioValidationException("A name is required.");
        var id = Guid.CreateVersion7();
        var slug = NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        var root = Path.IsPathRooted(options.Value.WorkspaceRoot)
            ? options.Value.WorkspaceRoot
            : Path.Combine(env.ContentRootPath, options.Value.WorkspaceRoot);

        return new WorkflowRun
        {
            Id = id,
            Name = name.Trim(),
            Source = source,
            Phase = phase,
            Status = RunStatus.Running,
            WorkspacePath = Path.Combine(root, $"{slug}-{id.ToString()[..8]}"),
        };
    }

    private async Task<WorkflowRun> QueueAsync(WorkflowRun run, JobKind job, string message, CancellationToken ct, WorkflowPhase? phase = null)
    {
        run.Phase = phase ?? run.Phase;
        run.Status = RunStatus.Running;
        run.StatusMessage = message;
        run.PendingJob = job.ToString(); // persisted first, so a crash before the worker picks it up is recoverable
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(new PhaseJob(run.Id, job), ct);
        hub.Publish(new RunNotification(run.Id, "status", message));
        return run;
    }

    private void RecordDecision(WorkflowRun run, bool approved, string? reason) =>
        db.Decisions.Add(new PhaseDecision { RunId = run.Id, Phase = run.Phase, Approved = approved, Reason = reason?.Trim() ?? "" });

    private async Task<WorkflowRun> LoadAsync(Guid runId, CancellationToken ct) =>
        await db.Runs.SingleOrDefaultAsync(r => r.Id == runId, ct) ?? throw new StudioNotFoundException("Workflow run not found.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new StudioValidationException(message);
    }
}

/// <summary>Mapped to HTTP 400 by the exception handler in Program.cs.</summary>
public sealed class StudioValidationException(string message) : Exception(message);

/// <summary>Mapped to HTTP 404 by the exception handler in Program.cs.</summary>
public sealed class StudioNotFoundException(string message) : Exception(message);
