using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Roster.Agents;

/// <summary>Who is running what, for which assignment. Everything the ledger and the tools need to scope their work.</summary>
public sealed record AgentRunContext(Guid AssignmentId, Guid UserId, string PhaseKey, string StepKey, int Attempt = 1)
{
    public string? TraceParent { get; init; }

    /// <summary>Per-call state the host's tools need (for example the retro turn they report into).</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
}

public sealed record EndpointCapabilities(bool NativeStructuredOutput, bool Tools, bool Streaming, bool Reasoning)
{
    public static EndpointCapabilities Default { get; } = new(NativeStructuredOutput: true, Tools: true, Streaming: true, Reasoning: false);
}

/// <summary>
/// A model endpoint after routing and after the secret has been decrypted in memory. Never serialise or log this: the
/// key is excluded from JSON and from <see cref="ToString"/>.
/// </summary>
public sealed record ResolvedModel(
    Guid? EndpointId,
    string EndpointName,
    string Kind,
    string? BaseUrl,
    string Model,
    [property: JsonIgnore] string? ApiKey,
    EndpointCapabilities Capabilities,
    int MaxConcurrency = 4)
{
    public override string ToString() => $"{EndpointName}/{Model}";
}

public interface IAgentCatalog
{
    IReadOnlyList<AgentDefinition> All { get; }

    AgentDefinition Get(string name);
}

/// <summary>Chooses the endpoint and model for one agent call. Implemented by the platform (routing needs the database and the vault).</summary>
public interface IModelResolver
{
    Task<ResolvedModel> ResolveAsync(AgentDefinition agent, AgentRunContext context, CancellationToken cancellationToken = default);
}

/// <summary>Maps capability ids to concrete tools. Implemented by the host so the library never knows what backs a capability.</summary>
public interface ICapabilityBinder
{
    IReadOnlyList<AITool> Bind(IReadOnlyCollection<string> capabilityIds, AgentRunContext context);
}

public sealed record InvocationStart(
    AgentRunContext Context,
    AgentDefinition Agent,
    ResolvedModel Model,
    string RuntimeKind,
    string OutputStrategy,
    string InputText);

public sealed record InvocationEnd(
    string Outcome,
    string? OutputJson,
    string? RawText,
    string? Error,
    string? ToolCallsJson,
    string? Reasoning,
    long? InputTokens,
    long? OutputTokens,
    long DurationMs,
    string? TraceId);

/// <summary>The ledger. The caller of an agent writes it, so history looks the same wherever the agent ran.</summary>
public interface IInvocationLedger
{
    Task<Guid> BeginAsync(InvocationStart start, CancellationToken cancellationToken = default);

    Task CompleteAsync(Guid invocationId, InvocationEnd end, CancellationToken cancellationToken = default);
}

public sealed record AgentResult<TOut>(TOut Output, Guid InvocationId);

public sealed record ChatTurn(string Role, string Content);

public sealed record ChatTurnResult(string Text, Guid InvocationId, string? Reasoning);

public sealed record ChatTurnOptions
{
    /// <summary>Called with the text produced so far while the model streams.</summary>
    public Func<string, Task>? OnPartial { get; init; }
}

public interface IAgentRunner
{
    /// <summary>Runs a typed agent: renders <paramref name="input"/>, applies the output strategy, validates, records the ledger row.</summary>
    Task<AgentResult<TOut>> RunAsync<TIn, TOut>(string agentName, TIn input, AgentRunContext context, CancellationToken cancellationToken = default)
        where TIn : class
        where TOut : class;

    /// <summary>Runs a conversational agent for one turn over the supplied history.</summary>
    Task<ChatTurnResult> ChatAsync(string agentName, IReadOnlyList<ChatTurn> history, AgentRunContext context, ChatTurnOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed class AgentRunException(string message, Guid invocationId, Exception? inner = null) : Exception(message, inner)
{
    public Guid InvocationId { get; } = invocationId;
}
