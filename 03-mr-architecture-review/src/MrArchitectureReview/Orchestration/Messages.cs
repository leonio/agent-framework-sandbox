using Microsoft.Agents.AI.Workflows;
using MrArchitectureReview.Domain;

namespace MrArchitectureReview.Orchestration;

/// <summary>Sent from the recorder to the publisher once the review record is on disk.</summary>
public sealed record RecordedReview(ReviewRecord Record, string RecordPath);

/// <summary>
/// A custom workflow event for progress messages.
/// </summary>
/// <remarks>
/// Executors call <c>context.AddEventAsync(new ReviewProgressEvent(...))</c>; the host receives it in
/// the same event stream as the framework's own events (<c>RequestInfoEvent</c>,
/// <c>WorkflowOutputEvent</c>, ...). WHY events rather than <c>Console.WriteLine</c> inside executors?
/// Executors stay UI-agnostic: the same workflow can drive this console app, a web UI over SignalR /
/// server-sent events (as sample 02 does), or a test that just collects the events.
/// </remarks>
public sealed class ReviewProgressEvent(string message) : WorkflowEvent(message)
{
    public string Message => message;
}

/// <summary>Keys for the workflow's shared state.</summary>
/// <remarks>
/// Shared state (<c>QueueStateUpdateAsync</c>/<c>ReadStateAsync</c>) lets executors that are not directly
/// connected see the same data, here the PR snapshot and run metadata, without threading them through
/// every message type. Updates are applied at the end of the superstep, so a value written in step N is
/// visible from step N+1. State is also included in checkpoints, so it survives a pause/resume.
/// Alternative: carry everything in the messages (more explicit, but every message type grows).
/// </remarks>
public static class StateKeys
{
    public const string Scope = "review";
    public const string Snapshot = "snapshot";
    public const string RunInfo = "run-info";
}

/// <summary>Run metadata stored in shared state by the first executor.</summary>
public sealed record RunInfo(string RunId, DateTimeOffset StartedAt);
