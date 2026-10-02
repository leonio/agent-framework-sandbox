using CliDesignPipeline.Ai;
using CliDesignPipeline.Cli;
using CliDesignPipeline.Content;
using CliDesignPipeline.Rag;
using CliDesignPipeline.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CliDesignPipeline.Agents;

/// <summary>Agent names. They match the <c>agent:</c> front matter in prompts/ and the mock scripts.</summary>
public static class AgentNames
{
    public const string Interviewer = "interviewer";
    public const string Developer = "developer";
    public const string Tester = "tester";
    public const string MergeRequest = "merge-request";
}

/// <summary>
/// Turns a prompt file + a tool list into an <see cref="AIAgent"/>.
/// </summary>
/// <remarks>
/// <para>
/// In Agent Framework an <see cref="AIAgent"/> is the unit you run, compose into workflows, host
/// behind an API, or expose as a tool to another agent. <see cref="ChatClientAgent"/> is the
/// implementation backed by any <see cref="IChatClient"/>: it owns the instructions, tools,
/// chat history (via <see cref="AgentSession"/>) and context providers, and runs the
/// model/tool loop.
/// </para>
/// <para>
/// Everything that makes the four agents <i>different</i> lives in data (their prompt file and
/// tool list); this factory is the only code that builds agents. That is what lets sample 02
/// load the same prompts from a database instead of disk without touching the workflow.
/// </para>
/// <para>
/// Alternatives:
/// </para>
/// <list type="bullet">
///   <item><b>Declarative agents</b> - define agents (and whole workflows) in YAML and load them with
///         <c>Microsoft.Agents.AI.Workflows.Declarative</c>. Great for low-code authoring; less
///         flexible for custom tools/providers like ours.</item>
///   <item><b>Server-side agents</b> (Azure AI Foundry) - the service stores instructions and
///         threads. Pick these when you want agents managed and versioned centrally.</item>
///   <item><b>Agent-as-tool</b> - <c>agent.AsAIFunction()</c> lets one agent call another like a
///         tool (an "orchestrator" pattern). We use an explicit workflow instead because the
///         order of steps is known up front and should not be left to a model.</item>
/// </list>
/// </remarks>
public sealed class PipelineAgentFactory(ChatClientFactory chatClients, PromptLibrary prompts, ConsoleUi ui)
{
    public AIAgent Create(string agentName, IEnumerable<AITool>? tools = null, IReadOnlyDictionary<string, string>? variables = null)
    {
        var prompt = prompts.GetPrompt(agentName);

        return chatClients.Create(agentName).AsAIAgent(new ChatClientAgentOptions
        {
            Name = agentName,
            Description = prompt.Description,
            ChatOptions = new ChatOptions
            {
                Instructions = prompts.ComposeInstructions(prompt, variables),
                // NOTE: reasoning models (o-series, gpt-5) reject temperature. If you use one, remove
                // the front-matter value or set it per model in configuration.
                Temperature = prompt.Temperature,
                Tools = tools is null ? null : ObservedFunction.WrapAll(tools, agentName, ui.ToolCall),
            },
            // Per-agent RAG over instructions/ (see InstructionsContextProvider for the production shape).
            AIContextProviders = [new InstructionsContextProvider(prompts, [.. prompt.PinnedInstructions])],
        });
    }
}
