using Microsoft.Extensions.AI;

namespace CliDesignPipeline.Tools;

/// <summary>
/// Decorates any <see cref="AIFunction"/> so every call is reported (to the console here; to
/// logs, metrics or an audit table in a real system).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DelegatingAIFunction"/> is the Microsoft.Extensions.AI base for this kind of
/// wrapper: the model still sees the inner function's name, description and schema.
/// </para>
/// <para>
/// Alternatives, from narrowest to widest scope:
/// </para>
/// <list type="bullet">
///   <item>this decorator - per tool, works with any agent type;</item>
///   <item><c>FunctionInvokingChatClient.FunctionInvoker</c> - one hook for every tool call of a chat client;</item>
///   <item>agent middleware (<c>agent.AsBuilder().Use(...)</c>) - can also inspect/deny calls and is
///         where human approval of risky tools (<c>ApprovalRequiredAIFunction</c>) is usually handled;</item>
///   <item>OpenTelemetry (<c>.UseOpenTelemetry()</c> on the chat client) - every tool call becomes a
///         span with arguments and results, viewable in Aspire Dashboard / App Insights.</item>
/// </list>
/// </remarks>
public sealed class ObservedFunction(AIFunction inner, string agentName, Action<string, string, string> onCall)
    : DelegatingAIFunction(inner)
{
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var summary = string.Join(", ", arguments.Select(a => $"{a.Key}={Shorten(a.Value?.ToString())}"));
        onCall(agentName, Name, summary);
        return base.InvokeCoreAsync(arguments, cancellationToken);
    }

    private static string Shorten(string? value) => value switch
    {
        null => "null",
        { Length: > 40 } => $"\"{value[..37].ReplaceLineEndings(" ")}...\"",
        _ => $"\"{value}\"",
    };

    /// <summary>Wraps every <see cref="AIFunction"/> in <paramref name="tools"/>; other tool kinds pass through.</summary>
    public static IList<AITool> WrapAll(IEnumerable<AITool> tools, string agentName, Action<string, string, string> onCall) =>
        [.. tools.Select(t => t is AIFunction f ? new ObservedFunction(f, agentName, onCall) : t)];
}
