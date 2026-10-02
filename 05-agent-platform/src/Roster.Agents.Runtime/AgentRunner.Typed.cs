using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Roster.Agents.Runtime;

public sealed partial class AgentRunner
{
    /// <summary>
    /// Runs a typed agent (one with input and output contracts, such as a reviewer) on <paramref name="input"/> and
    /// returns its validated output.
    /// </summary>
    /// <remarks>
    /// <para>Step by step:</para>
    /// <list type="number">
    /// <item>Check that <typeparamref name="TIn"/> and <typeparamref name="TOut"/> are the agent's contract types, so a
    /// scenario cannot hand a reviewer the wrong record.</item>
    /// <item>Pick the output strategy. The manifest asks (usually <c>native</c>); the endpoint decides. An endpoint
    /// without native structured output gets <c>prompted</c>: the schema goes into the instructions instead.</item>
    /// <item>Render the input with <see cref="PromptRenderer"/>, which fences untrusted fields, and open the ledger row.</item>
    /// <item>Run the agent in a session. If the reply is not valid JSON, does not match the schema or does not
    /// deserialize, send one repair request in the same session, quoting the problem, so the model sees its own reply
    /// and what was wrong with it. A second bad reply fails the call as <c>invalid-output</c>.</item>
    /// <item>Close the ledger row and return the output with the invocation id the platform links findings to.</item>
    /// </list>
    /// <para>This does not use Agent Framework's <c>RunAsync&lt;T&gt;</c>: it exists only on <c>ChatClientAgent</c>, decorators
    /// hide it, and it cannot do the prompted strategy (design doc section 5). The runner applies the schema and parses
    /// itself, which works the same for every endpoint.</para>
    /// </remarks>
    public async Task<AgentResult<TOut>> RunAsync<TIn, TOut>(
        string agentName, TIn input, AgentRunContext context, CancellationToken cancellationToken = default)
        where TIn : class
        where TOut : class
    {
        ArgumentNullException.ThrowIfNull(input);
        AgentDefinition agent = GetRunnableAgent(agentName);

        if (agent.InputType != typeof(TIn) || agent.OutputType != typeof(TOut))
        {
            throw new InvalidOperationException(
                $"Agent '{agent.Name}' takes {agent.InputType?.Name ?? "a conversation"} and returns {agent.OutputType?.Name ?? "text"}, " +
                $"not {typeof(TIn).Name} and {typeof(TOut).Name}." + (agent.OutputType is null ? " Use ChatAsync for conversational agents." : ""));
        }

        ResolvedModel model = await models.ResolveAsync(agent, context, cancellationToken);
        OutputStrategy strategy = ChooseStrategy(agent, model);
        ContractSchema schema = ContractSchemas.For(typeof(TOut));

        string inputText = PromptRenderer.Render(input);
        string instructions = strategy == OutputStrategy.Prompted
            ? agent.Instructions + StructuredOutput.PromptedInstructions(schema)
            : agent.Instructions;
        ChatResponseFormat? responseFormat = strategy == OutputStrategy.Native
            ? ChatResponseFormat.ForJsonSchema(schema.Json, schema.Name)
            : null;

        Guid invocationId = await ledger.BeginAsync(
            new InvocationStart(context, agent, model, RuntimeKind.Chat.ToString().ToLowerInvariant(), strategy.ToString().ToLowerInvariant(), inputText),
            cancellationToken);

        logger.LogInformation("Running {Agent} ({Hash}) on {Model} with {Strategy} output, invocation {InvocationId}",
            agent.Name, agent.ShortHash, model, strategy, invocationId);

        var capture = new RunCapture();
        string? rawText = null;
        try
        {
            ChatClientAgent runnable = BuildAgent(agent, model, instructions, BindTools(agent, context), responseFormat);
            AgentSession session = await runnable.CreateSessionAsync(cancellationToken);

            // Attempt 1: the task itself.
            AgentResponse first = await runnable.RunAsync(inputText, session, cancellationToken: cancellationToken);
            Capture(capture, first);
            rawText = first.Text;
            if (TryParse(rawText, schema, out TOut? output, out string problem))
            {
                return await SucceedAsync(Outcomes.Succeeded);
            }

            // Attempt 2: one repair request in the same session, so the model sees its reply and what was wrong.
            logger.LogInformation("Agent {Agent} gave an unusable reply ({Problem}); asking once for a repair", agent.Name, problem);
            AgentResponse repair = await runnable.RunAsync(StructuredOutput.RepairRequest(problem), session, cancellationToken: cancellationToken);
            Capture(capture, repair);
            rawText = repair.Text;
            if (TryParse(rawText, schema, out output, out problem))
            {
                return await SucceedAsync(Outcomes.Repaired);
            }

            await RecordFailureAsync(invocationId, agent, Outcomes.InvalidOutput, null, $"Unusable output after one repair attempt: {problem}", capture, rawText);
            throw new AgentRunException($"Agent '{agent.Name}' returned unusable output after one repair attempt: {problem}", invocationId);

            async Task<AgentResult<TOut>> SucceedAsync(string outcome)
            {
                string outputJson = JsonSerializer.Serialize(output, ContractJson.Options);
                await ledger.CompleteAsync(invocationId, capture.ToEnd(outcome, outputJson, rawText, error: null), CancellationToken.None);
                logger.LogInformation("Agent {Agent} ({Hash}) {Outcome}, invocation {InvocationId}", agent.Name, agent.ShortHash, outcome, invocationId);
                return new AgentResult<TOut>(output!, invocationId);
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            await RecordFailureAsync(invocationId, agent, Outcomes.Cancelled, ex, "Cancelled.", capture, rawText);
            throw;
        }
        catch (Exception ex) when (ex is not AgentRunException)
        {
            await RecordFailureAsync(invocationId, agent, Outcomes.Failed, ex, ex.Message, capture, rawText);
            throw new AgentRunException($"Agent '{agent.Name}' failed: {ex.Message}", invocationId, ex);
        }
    }

    /// <summary>
    /// The manifest says what the agent prefers; the endpoint decides what is possible. <c>native</c> and <c>tool</c>
    /// both use the endpoint's structured output when it has it (a terminal tool is the Copilot runtime's fallback, not
    /// needed for chat endpoints). Without it, everything falls back to <c>prompted</c>.
    /// </summary>
    private static OutputStrategy ChooseStrategy(AgentDefinition agent, ResolvedModel model) =>
        agent.Manifest.OutputStrategy == OutputStrategy.Prompted || !model.Capabilities.NativeStructuredOutput
            ? OutputStrategy.Prompted
            : OutputStrategy.Native;

    private static void Capture(RunCapture capture, AgentResponse response)
    {
        capture.Add(response.Messages.SelectMany(m => m.Contents));

        // Usage normally arrives on the response itself; UsageContent inside messages is the streaming form.
        if (!response.Messages.SelectMany(m => m.Contents).OfType<UsageContent>().Any())
        {
            capture.Add(response.Usage);
        }
    }

    /// <summary>
    /// Turns a reply into <typeparamref name="TOut"/>: extract the JSON object, check it against the schema, then
    /// deserialize. On failure, <paramref name="problem"/> says what was wrong in words the repair request can quote.
    /// </summary>
    private static bool TryParse<TOut>(string text, ContractSchema schema, out TOut? output, out string problem)
        where TOut : class
    {
        output = null;
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(StructuredOutput.ExtractJson(text));
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            problem = $"it is not valid JSON ({ex.Message})";
            return false;
        }

        if (!schema.TryValidate(root, out string errors))
        {
            problem = $"it does not match the schema: {errors}";
            return false;
        }

        try
        {
            output = root.Deserialize<TOut>(ContractJson.Options);
        }
        catch (JsonException ex)
        {
            problem = $"it could not be read as {schema.Name} ({ex.Message})";
            return false;
        }

        problem = output is null ? "it was empty" : "";
        return output is not null;
    }
}
