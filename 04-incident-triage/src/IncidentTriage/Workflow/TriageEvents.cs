using Microsoft.Agents.AI.Workflows;

namespace IncidentTriage.Workflow;

/// <summary>
/// A custom workflow event: "step X says Y". Executors raise it; the host prints it.
/// </summary>
/// <remarks>
/// <para><b>WHY events instead of Console.WriteLine in executors?</b> The same workflow can be hosted in a
/// console, an ASP.NET Core endpoint streaming Server-Sent Events, Azure Functions, or the Agent Framework
/// DevUI. Executors raise events into the run's stream (<c>StreamingRun.WatchStreamAsync()</c>) and each host
/// renders them its own way. Built-in events (ExecutorInvokedEvent, AgentResponseUpdateEvent,
/// RequestInfoEvent, WorkflowOutputEvent...) flow through the same stream.</para>
/// </remarks>
public sealed class TriageProgressEvent(string step, string message) : WorkflowEvent(message)
{
    public string Step { get; } = step;
    public string Message { get; } = message;
}

/// <summary>
/// C# 14 extension members. An <c>extension(T receiver)</c> block declares extensions for a type in one
/// place, and (new in C# 14) can include extension <i>properties</i> and static members, not just methods.
/// Here it gives every executor a terse <c>context.ReportAsync(...)</c> without a base class.
/// Docs: https://learn.microsoft.com/dotnet/csharp/language-reference/proposals/csharp-14.0/extensions
/// Alternative: a shared abstract executor base class, which forces a class hierarchy just for logging.
/// </summary>
public static class WorkflowContextExtensions
{
    extension(IWorkflowContext context)
    {
        public ValueTask ReportAsync(string step, string message, CancellationToken cancellationToken = default)
            => context.AddEventAsync(new TriageProgressEvent(step, message), cancellationToken);
    }
}
