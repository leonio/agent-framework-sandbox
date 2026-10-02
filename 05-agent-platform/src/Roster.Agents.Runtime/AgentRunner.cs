using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Roster.Agents.Runtime;

public sealed class AgentRunnerOptions
{
    /// <summary>
    /// The isolation this process provides, checked against each agent's minimum placement before it runs. A runner in
    /// the general pool is <see cref="Placement.InProcess"/>; one in the locked-down sandbox pool is
    /// <see cref="Placement.Pool"/>. An agent that needs more than the host offers is refused, never run anyway.
    /// </summary>
    public Placement HostPlacement { get; set; } = Placement.InProcess;
}

/// <summary>
/// Runs library agents for the platform: typed agents through <see cref="RunAsync{TIn, TOut}"/> (in
/// <c>AgentRunner.Typed.cs</c>) and conversational agents through <see cref="ChatAsync"/> (in
/// <c>AgentRunner.Chat.cs</c>). This file holds the steps both share.
/// </summary>
/// <remarks>
/// <para>Every call follows the same outline:</para>
/// <list type="number">
/// <item>Look the agent up and refuse it if this host cannot run it (runtime kind, placement).</item>
/// <item>Resolve the endpoint and model (<see cref="IModelResolver"/>, implemented by the platform) and bind the
/// agent's capabilities to tools (<see cref="ICapabilityBinder"/>, also the platform).</item>
/// <item>Open a ledger row (<see cref="IInvocationLedger"/>) with the agent's hash, the model and the rendered input.</item>
/// <item>Build a fresh Agent Framework <see cref="ChatClientAgent"/> for this one call and run it.</item>
/// <item>Close the ledger row with the outcome, output, tool calls, reasoning, tokens and duration, whether the call
/// succeeded, failed or was cancelled.</item>
/// </list>
/// <para>Scoped, because the platform services it depends on are scoped (they use the database).</para>
/// </remarks>
public sealed partial class AgentRunner(
    IAgentCatalog catalog,
    IModelResolver models,
    ICapabilityBinder capabilities,
    IInvocationLedger ledger,
    ChatClientFactory clients,
    IOptions<AgentRunnerOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<AgentRunner> logger)
{
    /// <summary>Ledger outcomes. Scorecards count them, so the spelling is part of the contract.</summary>
    public static class Outcomes
    {
        public const string Succeeded = "succeeded";

        /// <summary>The first reply was unusable and the repair attempt fixed it. A quality signal worth counting.</summary>
        public const string Repaired = "repaired";

        public const string InvalidOutput = "invalid-output";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    /// <summary>Finds the agent and checks that this host may run it. Throws before anything is recorded if not.</summary>
    private AgentDefinition GetRunnableAgent(string agentName)
    {
        AgentDefinition agent = catalog.Get(agentName);

        // Slice 1 runs chat agents only. Copilot agents need the sandbox pool (slice 2); remote agents go through A2A.
        if (agent.Manifest.Runtime != RuntimeKind.Chat)
        {
            throw new NotSupportedException(
                $"Agent '{agent.Name}' uses the {agent.Manifest.Runtime} runtime, which this runner does not support yet.");
        }

        // The placement rule from the design doc: the agent's riskiest capability (and its runtime) set the minimum
        // isolation. A host offering less refuses to run it.
        Placement required = PlacementPolicy.Minimum(agent.Manifest);
        if (!PlacementPolicy.Satisfies(options.Value.HostPlacement, required))
        {
            throw new InvalidOperationException(
                $"Agent '{agent.Name}' needs placement {required}, but this host provides {options.Value.HostPlacement}. " +
                "Run it from a runner in the right pool.");
        }

        return agent;
    }

    /// <summary>
    /// A new Agent Framework agent for one call. Building per call is cheap and keeps calls independent: each has its
    /// own model, tools, instructions and (through the session) history.
    /// </summary>
    private ChatClientAgent BuildAgent(
        AgentDefinition agent, ResolvedModel model, string instructions, IReadOnlyList<AITool> tools, ChatResponseFormat? responseFormat) =>
        new(clients.Create(model, agent), new ChatClientAgentOptions
        {
            Name = agent.Name,
            Description = agent.Manifest.Description,
            ChatOptions = new ChatOptions
            {
                ModelId = model.Model,
                Instructions = instructions,
                Tools = tools.Count > 0 ? [.. tools] : null,
                ResponseFormat = responseFormat,
            },
        }, loggerFactory);

    private IReadOnlyList<AITool> BindTools(AgentDefinition agent, AgentRunContext context) =>
        agent.Manifest.Capabilities.Count == 0 ? [] : capabilities.Bind(agent.Manifest.Capabilities, context);

    /// <summary>Closes the ledger row for a call that did not succeed, and logs why.</summary>
    private async Task RecordFailureAsync(
        Guid invocationId, AgentDefinition agent, string outcome, Exception? error, string message, RunCapture capture, string? rawText)
    {
        logger.LogWarning(error, "Agent {Agent} ({Hash}) ended {Outcome}: {Message}", agent.Name, agent.ShortHash, outcome, message);

        // CancellationToken.None: the row must be closed even when the call itself was cancelled.
        await ledger.CompleteAsync(invocationId, capture.ToEnd(outcome, outputJson: null, rawText, message), CancellationToken.None);
    }

    /// <summary>
    /// Collects what a call produced besides its answer, across every model round trip and attempt: tool calls with
    /// their results, reasoning text, and token usage. It becomes the closing half of the ledger row.
    /// </summary>
    private sealed class RunCapture
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<FunctionCallContent> _calls = [];
        private readonly Dictionary<string, object?> _results = [];
        private readonly StringBuilder _reasoning = new();
        private long? _inputTokens;
        private long? _outputTokens;

        public string? Reasoning => _reasoning.Length == 0 ? null : _reasoning.ToString().Trim();

        public void Add(IEnumerable<AIContent> contents)
        {
            foreach (AIContent content in contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        _calls.Add(call);
                        break;
                    case FunctionResultContent result:
                        _results[result.CallId] = result.Exception?.Message ?? result.Result;
                        break;
                    case TextReasoningContent reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                        _reasoning.Append(reasoning.Text).Append('\n');
                        break;
                    case UsageContent usage:
                        Add(usage.Details);
                        break;
                }
            }
        }

        public void Add(UsageDetails? usage)
        {
            if (usage is null)
            {
                return;
            }

            _inputTokens = Sum(_inputTokens, usage.InputTokenCount);
            _outputTokens = Sum(_outputTokens, usage.OutputTokenCount);
        }

        public InvocationEnd ToEnd(string outcome, string? outputJson, string? rawText, string? error) => new(
            Outcome: outcome,
            OutputJson: outputJson,
            RawText: rawText,
            Error: error,
            ToolCallsJson: ToolCallsJson(),
            Reasoning: Reasoning,
            InputTokens: _inputTokens,
            OutputTokens: _outputTokens,
            DurationMs: _clock.ElapsedMilliseconds,
            TraceId: Activity.Current?.TraceId.ToString());

        // [{ "name": "get_step", "arguments": { ... }, "result": ... }, ...] in call order. The retro's get_step shows it.
        private string? ToolCallsJson()
        {
            if (_calls.Count == 0)
            {
                return null;
            }

            var rows = _calls.Select(c => new
            {
                name = c.Name,
                arguments = c.Arguments,
                result = _results.GetValueOrDefault(c.CallId),
            });
            return JsonSerializer.Serialize(rows, ContractJson.Options);
        }

        private static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
    }
}
