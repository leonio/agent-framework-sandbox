using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SdlcStudio.Api.Ai;

/// <summary>
/// Builds Agent Framework agents (<see cref="ChatClientAgent"/>) from the instructions and skills
/// stored in the Admin area.
/// </summary>
/// <remarks>
/// <para>
/// WHY create agents per job instead of once at startup?
/// Agents are cheap objects (instructions + a chat client + tools). Creating them when a phase runs
/// means an instruction edited in the Admin UI takes effect on the very next run, with no restart.
/// </para>
/// <para>
/// WHY many small single-purpose agents rather than one "do everything" agent?
/// Each agent has a short, focused system prompt, its own tools and a typed output. That makes them
/// easier to test, cheaper to run (smaller prompts), and lets the workflow, not the model, decide
/// what happens next. This is the core idea this sample demonstrates.
/// </para>
/// </remarks>
public sealed class AgentFactory(PromptLibrary prompts, ChatClientFactory chatClients, ILoggerFactory loggerFactory)
{
    public async Task<ChatClientAgent> CreateAsync(string agentKey, IList<AITool>? tools = null, CancellationToken ct = default)
    {
        var (instructions, version) = await prompts.GetAgentInstructionsAsync(agentKey, ct);

        return chatClients.Create(agentKey).AsAIAgent(
            new ChatClientAgentOptions
            {
                Name = agentKey,
                Description = $"{agentKey} (instructions v{version})",
                ChatOptions = new ChatOptions
                {
                    Instructions = instructions,
                    Tools = tools,
                    // Low temperature: these agents produce specs and code, not creative writing.
                    // Some reasoning models reject temperature entirely; remove it if yours does.
                    Temperature = 0.2f,
                },
                // ALTERNATIVES you can plug in here:
                //  - ChatHistoryProvider: persist conversation history somewhere other than the session
                //    (e.g. a database or Cosmos DB) for very long conversations.
                //  - AIContextProviders: inject extra context on every call. This is the natural home for
                //    RAG (a TextSearchProvider over a vector store) or for long-term "memory" components.
            },
            loggerFactory);
    }
}
