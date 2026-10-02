using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Roster.Agents.Runtime;

public sealed partial class AgentRunner : IAgentRunner
{
    /// <summary>
    /// Runs one turn of a conversational agent (one without contracts, such as the retro facilitator) over the
    /// conversation so far, and returns its reply.
    /// </summary>
    /// <remarks>
    /// <para>The platform owns the conversation: it stores the messages and passes the whole history on every turn,
    /// so a turn can run on any runner replica and nothing lives in memory between turns. The agent may call its tools
    /// during the turn (the facilitator reads the assignment and proposes cards); Agent Framework's function-invoking
    /// loop runs them, and the runner records each call and its result.</para>
    /// <para>When the endpoint streams, <see cref="ChatTurnOptions.OnPartial"/> is called with the reply so far after
    /// each piece of text, so the UI can show it as it is written. Throttling those updates is the caller's job (the
    /// retro writes at most every 250 ms). Endpoints that do not stream get one call with the whole reply.</para>
    /// <para>The person's own messages are not fenced as untrusted: in a conversation the person is who the agent
    /// works for. What the tools return is data, and the agent's instructions (the untrusted-input skill) say so.</para>
    /// </remarks>
    public async Task<ChatTurnResult> ChatAsync(
        string agentName, IReadOnlyList<ChatTurn> history, AgentRunContext context, ChatTurnOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        AgentDefinition agent = GetRunnableAgent(agentName);
        if (agent.OutputType is not null)
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' is a typed agent; run it with RunAsync.");
        }

        ResolvedModel model = await models.ResolveAsync(agent, context, cancellationToken);
        List<ChatMessage> messages = [.. history.Select(ToChatMessage)];

        // The ledger keeps the conversation as the model saw it on this turn.
        string transcript = string.Join("\n\n", history.Select(t => $"{t.Role}: {t.Content}"));
        Guid invocationId = await ledger.BeginAsync(
            new InvocationStart(context, agent, model, RuntimeKind.Chat.ToString().ToLowerInvariant(), "text", transcript),
            cancellationToken);

        logger.LogInformation("Chat turn for {Agent} ({Hash}) on {Model}, {Turns} message(s) of history, invocation {InvocationId}",
            agent.Name, agent.ShortHash, model, history.Count, invocationId);

        var capture = new RunCapture();
        var text = new StringBuilder();
        try
        {
            ChatClientAgent runnable = BuildAgent(agent, model, agent.Instructions, BindTools(agent, context), responseFormat: null);

            if (model.Capabilities.Streaming)
            {
                await foreach (AgentResponseUpdate update in runnable.RunStreamingAsync(messages, cancellationToken: cancellationToken))
                {
                    capture.Add(update.Contents);

                    // Only the assistant's own words go into the reply; tool results stream through here too.
                    if (update.Role != ChatRole.Tool && update.Text is { Length: > 0 } piece)
                    {
                        text.Append(piece);
                        if (options?.OnPartial is { } onPartial)
                        {
                            await onPartial(text.ToString());
                        }
                    }
                }
            }
            else
            {
                AgentResponse response = await runnable.RunAsync(messages, cancellationToken: cancellationToken);
                Capture(capture, response);
                text.Append(response.Text);
                if (options?.OnPartial is { } onPartial)
                {
                    await onPartial(text.ToString());
                }
            }

            string reply = text.ToString().Trim();
            await ledger.CompleteAsync(invocationId, capture.ToEnd(Outcomes.Succeeded, outputJson: null, reply, error: null), CancellationToken.None);
            logger.LogInformation("Chat turn for {Agent} succeeded, invocation {InvocationId}", agent.Name, invocationId);
            return new ChatTurnResult(reply, invocationId, capture.Reasoning);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            await RecordFailureAsync(invocationId, agent, Outcomes.Cancelled, ex, "Cancelled.", capture, text.ToString());
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(invocationId, agent, Outcomes.Failed, ex, ex.Message, capture, text.ToString());
            throw new AgentRunException($"Chat turn for '{agent.Name}' failed: {ex.Message}", invocationId, ex);
        }
    }

    // History roles are plain strings from the platform's store. Anything that is not the assistant or a system note
    // is the person.
    private static ChatMessage ToChatMessage(ChatTurn turn) => turn.Role.ToLowerInvariant() switch
    {
        "assistant" => new ChatMessage(ChatRole.Assistant, turn.Content),
        "system" => new ChatMessage(ChatRole.System, turn.Content),
        _ => new ChatMessage(ChatRole.User, turn.Content),
    };
}
