using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using SdlcStudio.Api.Domain;
using SdlcStudio.Api.Tools;

namespace SdlcStudio.Api.Workflows;

// =============================================================================================
// Develop workflow: implements ONE story, task by task, as small reviewed commits, then raises a
// merge request checkpoint. The human approves each checkpoint before the next story starts.
//
//   StoryWork --> [dispatch tasks] --TaskAssignment--> [developer] --> [code reviewer]
//                     ^      |                              ^              |  |
//                     |      |                              |   rejected   |  | approved
//                     |      |                     [request changes] <-----+  v
//                     |      |                                             [commit]
//                     +------|---------------- TaskCommitted ----------------+
//                            |
//                            +--StoryCompleted--> [merge request checkpoint] --> StoryDelivered
//
// Two loops: the inner developer <-> reviewer loop (max 3 attempts per task) and the outer
// "next task" loop driven by the dispatcher. Messages are routed by *type*: the dispatcher has
// edges to both the developer and the MR step, and each only receives the message types it handles.
//
// WHY "bit by bit"? Small commits that each passed review are easy for humans to audit and easy to
// revert, and an MR per story keeps a human in the loop at a natural review size.
// =============================================================================================

public sealed record TaskInput(int TaskId, int Order, string Title, string Detail, string Files);
public sealed record StoryWork(StoryInput Story, List<TaskInput> Tasks, string Branch, string? Feedback);
public sealed record TaskAssignment(StoryWork Work, TaskInput Task, int Index, int Count, int Attempt, List<string> ReviewComments);
public sealed record ProposedChange(TaskAssignment Assignment, CodeChange Change);
public sealed record ReviewedChange(ProposedChange Proposal, CodeReview Review);
public sealed record TaskCommitted(TaskInput Task, string Sha, string Message, bool ApprovedByReviewer);
public sealed record StoryCompleted(StoryWork Work, List<TaskCommitted> Commits);
public sealed record StoryDelivered(StoryInput Story, string Branch, List<TaskCommitted> Commits, string MergeRequest);

public static class DevelopWorkflow
{
    public const int MaxReviewAttempts = 3;

    public static Workflow Build(RunContext run, AIAgent developer, AIAgent reviewer, GitWorkspace git, MergeRequestPublisher mr)
    {
        var dispatch = new TaskDispatcherExecutor(run, git);
        var develop = new DeveloperExecutor(run, developer);
        var review = new CodeReviewerExecutor(run, reviewer);
        var commit = new CommitExecutor(run, git);
        var raiseMr = new MergeRequestExecutor(run, git, mr);

        var requestChanges = ExecutorBindingExtensions.BindAsExecutor<ReviewedChange, TaskAssignment>(
            r => r.Proposal.Assignment with { Attempt = r.Proposal.Assignment.Attempt + 1, ReviewComments = r.Review.Comments },
            "request-changes");

        static bool ShouldCommit(ReviewedChange? r) => r!.Review.Approved || r.Proposal.Assignment.Attempt >= MaxReviewAttempts;

        return new WorkflowBuilder(dispatch)
            .WithName("develop-story")
            .AddEdge(dispatch, develop)                 // TaskAssignment
            .AddEdge(dispatch, raiseMr)                 // StoryCompleted
            .AddEdge(develop, review)
            .AddEdge<ReviewedChange>(review, commit, ShouldCommit)
            .AddEdge<ReviewedChange>(review, requestChanges, r => !ShouldCommit(r))
            .AddEdge(requestChanges, develop)
            .AddEdge(commit, dispatch)                  // TaskCommitted -> next task
            .WithOutputFrom(raiseMr)
            .Build();
    }
}

/// <summary>
/// Drives the outer loop. Handles two message types, so it derives from the non-generic
/// <see cref="Executor"/> and declares its routes in <see cref="ConfigureProtocol"/>.
/// </summary>
/// <remarks>
/// State (remaining tasks, commits so far) lives in *workflow state* rather than fields.
/// WHY: workflow state is scoped to the run and is what checkpointing captures, so this executor would
/// survive a pause/resume if you add a CheckpointManager. Updates are queued and become visible at the
/// end of the superstep, which suits a loop where each iteration is a later superstep.
/// </remarks>
internal sealed class TaskDispatcherExecutor(RunContext run, GitWorkspace git) : Executor("dispatch-tasks")
{
    private const string RemainingKey = "remaining";
    private const string CommitsKey = "commits";
    private const string WorkKey = "work";

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) => protocol
        .ConfigureRoutes(routes => routes
            .AddHandler<StoryWork>(StartAsync)
            .AddHandler<TaskCommitted>(NextAsync))
        .SendsMessage<TaskAssignment>()
        .SendsMessage<StoryCompleted>();

    private async ValueTask StartAsync(StoryWork work, IWorkflowContext context, CancellationToken ct)
    {
        await git.EnsureRepositoryAsync(run.RepoFolder, ct);
        await git.CheckoutStoryBranchAsync(run.RepoFolder, work.Branch, ct);
        await context.AddEventAsync(new StudioProgressEvent($"{work.Story.Key}: {work.Tasks.Count} task(s) on branch {work.Branch}"), ct);

        await context.QueueStateUpdateAsync(WorkKey, work, ct);
        await context.QueueStateUpdateAsync(RemainingKey, work.Tasks.Skip(1).ToList(), ct);
        await context.QueueStateUpdateAsync(CommitsKey, new List<TaskCommitted>(), ct);
        await context.SendMessageAsync(new TaskAssignment(work, work.Tasks[0], 1, work.Tasks.Count, 1, []), ct);
    }

    private async ValueTask NextAsync(TaskCommitted committed, IWorkflowContext context, CancellationToken ct)
    {
        var work = (await context.ReadStateAsync<StoryWork>(WorkKey, ct))!;
        var remaining = await context.ReadStateAsync<List<TaskInput>>(RemainingKey, ct) ?? [];
        var commits = await context.ReadStateAsync<List<TaskCommitted>>(CommitsKey, ct) ?? [];

        commits = [.. commits, committed];
        await context.QueueStateUpdateAsync(CommitsKey, commits, ct);

        if (remaining is [var next, .. var rest]) // list pattern: take the head, keep the tail
        {
            await context.QueueStateUpdateAsync(RemainingKey, rest, ct);
            await context.SendMessageAsync(new TaskAssignment(work, next, commits.Count + 1, work.Tasks.Count, 1, []), ct);
        }
        else
        {
            await context.SendMessageAsync(new StoryCompleted(work, commits), ct);
        }
    }
}

/// <summary>Agent step with a tool (update_jira_ticket) and structured output (CodeChange).</summary>
internal sealed class DeveloperExecutor(RunContext run, AIAgent agent) : Executor<TaskAssignment, ProposedChange>("developer")
{
    public override async ValueTask<ProposedChange> HandleAsync(TaskAssignment message, IWorkflowContext context, CancellationToken ct = default)
    {
        await context.AddEventAsync(new StudioProgressEvent(
            $"Developer: task {message.Index}/{message.Count} '{message.Task.Title}' (attempt {message.Attempt})"), ct);

        // RAG OPPORTUNITY: retrieve the most similar existing files from the repository (embedding search
        // over the code base) and include them, so generated code follows the project's existing patterns.
        var prompt = run.Prompt("implement-task",
            ("attempt", message.Attempt.ToString()),
            ("storyKey", message.Work.Story.Key),
            ("story", message.Work.Story.ToMarkdown()),
            ("task", $"Task {message.Index} of {message.Count}: {message.Task.Title}\n{message.Task.Detail}\nExpected files: {message.Task.Files}"),
            ("reviewComments", string.Join("\n", message.ReviewComments.Select(c => $"- {c}"))),
            ("feedback", message.Work.Feedback));

        var response = await agent.RunAsync<CodeChange>(prompt, serializerOptions: StudioJson.Options, cancellationToken: ct);
        return new ProposedChange(message, response.Result);
    }
}

/// <summary>Agent step: a second agent reviews the first agent's work before anything is committed.</summary>
internal sealed class CodeReviewerExecutor(RunContext run, AIAgent agent) : Executor<ProposedChange, ReviewedChange>("code-reviewer")
{
    public override async ValueTask<ReviewedChange> HandleAsync(ProposedChange message, IWorkflowContext context, CancellationToken ct = default)
    {
        var a = message.Assignment;
        var change = new StringBuilder($"Commit message: {message.Change.CommitMessage}\nNotes: {message.Change.Notes}\n");
        foreach (var f in message.Change.Files) change.AppendLine($"\n--- {f.Path}\n{f.Content}");

        var prompt = run.Prompt("review-code",
            ("attempt", a.Attempt.ToString()),
            ("task", $"Task {a.Index} of {a.Count}: {a.Task.Title}\n{a.Task.Detail}"),
            ("change", change.ToString()));

        var review = (await agent.RunAsync<CodeReview>(prompt, serializerOptions: StudioJson.Options, cancellationToken: ct)).Result;

        await context.AddEventAsync(new StudioProgressEvent(review.Approved
            ? $"Code review approved task {a.Index} (attempt {a.Attempt})"
            : $"Code review requested changes on task {a.Index}: {string.Join(" ", review.Comments)}"), ct);

        return new ReviewedChange(message, review);
    }
}

/// <summary>Plain code step: writes the files and makes one small commit.</summary>
internal sealed class CommitExecutor(RunContext run, GitWorkspace git) : Executor<ReviewedChange, TaskCommitted>("commit")
{
    public override async ValueTask<TaskCommitted> HandleAsync(ReviewedChange message, IWorkflowContext context, CancellationToken ct = default)
    {
        var change = message.Proposal.Change;
        var sha = await git.CommitAsync(run.RepoFolder, change.Files.Select(f => (f.Path, f.Content)), change.CommitMessage, ct);

        var note = message.Review.Approved ? "" : " (committed after max review attempts; flagged for human review)";
        await context.AddEventAsync(new StudioProgressEvent($"Committed {sha}: {change.CommitMessage}{note}"), ct);

        return new TaskCommitted(message.Proposal.Assignment.Task, sha, change.CommitMessage, message.Review.Approved);
    }
}

/// <summary>Plain code step: produces the merge request checkpoint for the human gate.</summary>
[YieldsOutput(typeof(StoryDelivered))]
internal sealed class MergeRequestExecutor(RunContext run, GitWorkspace git, MergeRequestPublisher mr) : Executor<StoryCompleted>("merge-request")
{
    public override async ValueTask HandleAsync(StoryCompleted message, IWorkflowContext context, CancellationToken ct = default)
    {
        // Workflow state is private to the executor that wrote it (scoped by executor id), so the
        // dispatcher hands the commit list over in the message rather than us reading its state.
        var (work, commits) = message;
        var diff = await git.DiffStatAsync(run.RepoFolder, work.Branch, ct);
        var instructions = mr.BuildInstructions(run.RepoFolder, work.Branch, work.Story.Key, work.Story.Title,
            commits.Select(c => $"`{c.Sha}` {c.Message}"), diff);

        await context.AddEventAsync(new StudioProgressEvent($"Merge request checkpoint ready for {work.Story.Key} ({commits.Count} commits)"), ct);
        await context.YieldOutputAsync(new StoryDelivered(work.Story, work.Branch, commits, instructions), ct);
    }
}
