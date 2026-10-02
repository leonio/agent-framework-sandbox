using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MrArchitectureReview.Ai;
using MrArchitectureReview.Configuration;
using MrArchitectureReview.Domain;
using MrArchitectureReview.Integrations;
using MrArchitectureReview.Memory;
using MrArchitectureReview.Persistence;
using MrArchitectureReview.Prompts;
using MrArchitectureReview.Rag;
using MrArchitectureReview.Tools;

namespace MrArchitectureReview.Orchestration;

/// <summary>
/// Builds the agents. All agent <i>composition</i> (model + instructions + tools + context providers) lives
/// here, so the executors only deal with running them.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY create agents per PR rather than once at startup?</b> A reviewer's tools are bound to <i>this</i>
/// PR's checkout, and its memory provider to <i>this</i> repository. A <see cref="ChatClientAgent"/> is a
/// lightweight object over a shared <see cref="IChatClient"/>, so building one per run costs nothing.
/// Alternative: one long-lived agent per aspect, passing the checkout path via an <c>AgentSession</c>
/// or tool arguments. That works, but leaks per-run state into long-lived objects.
/// </para>
/// <para>
/// <b>Anatomy of a <see cref="ChatClientAgent"/>:</b>
/// <list type="bullet">
///   <item><c>ChatOptions.Instructions</c>: the system prompt (from <c>Prompts/*.md</c>).</item>
///   <item><c>ChatOptions.Tools</c>: functions the model may call; invoked automatically.</item>
///   <item><c>AIContextProviders</c>: per-call context injection (memory, RAG).</item>
/// </list>
/// We do not set <c>Temperature</c>: reasoning models (o-series, gpt-5) reject it. For classic chat
/// models a low temperature (0-0.2) makes reviews more repeatable.
/// </para>
/// <para>
/// Other agent types the same code could use with no workflow changes, because executors only see
/// <see cref="AIAgent"/>: Azure AI Foundry hosted agents (server-side threads, built-in tools),
/// OpenAI Assistants/Responses agents, A2A remote agents, or a Copilot Studio agent.
/// </para>
/// </remarks>
public sealed class ReviewerAgentFactory(
    ChatClientFactory chatClients,
    PromptLibrary prompts,
    ReviewStore store,
    GuidelinesSearch guidelines,
    PublisherTools publisherTools,
    GitHubMcpTools gitHubMcp,
    IOptions<ReviewOptions> options)
{
    public (AIAgent Agent, Prompt Prompt) CreateReviewer(ReviewAspect aspect, PullRequestSnapshot snapshot)
    {
        var prompt = prompts.Load(aspect.AgentName);
        var agent = new ChatClientAgent(
            chatClients.CreateFor(aspect.AgentName),
            new ChatClientAgentOptions
            {
                Name = aspect.AgentName,
                Description = $"Surface-level {aspect} reviewer",
                ChatOptions = new ChatOptions
                {
                    Instructions = prompt.Instructions,
                    Tools = new RepositoryTools(snapshot.LocalCheckoutPath).AsAITools(),
                },
                AIContextProviders =
                [
                    // Memory: what humans said about this reviewer's past findings on this repo.
                    new ReviewHistoryContextProvider(store, snapshot.RepositoryKey, aspect, options.Value.Settings.HistoryExamples),
                    // RAG: the team guidelines relevant to this diff.
                    guidelines.CreateProvider(),
                ],
            });

        return (agent, prompt);
    }

    public async Task<AIAgent> CreatePublisherAsync(CancellationToken cancellationToken)
    {
        var prompt = prompts.Load("publisher");
        IList<AITool> tools = [.. publisherTools.AsAITools(), .. await gitHubMcp.GetToolsAsync(cancellationToken)];

        // The short form: IChatClient.AsAIAgent(...) is an extension that builds the same ChatClientAgent.
        return chatClients.CreateFor("publisher").AsAIAgent(
            name: "publisher",
            instructions: prompt.Instructions,
            tools: tools);
    }

    /// <summary>
    /// The user message every reviewer receives. The PR content is fenced and labelled as untrusted,
    /// which (together with the rule in the system prompt) makes prompt injection in a PR description
    /// much less likely to work. It is a mitigation, not a guarantee: never give reviewer agents tools
    /// that can change anything.
    /// </summary>
    public static string BuildReviewMessage(PullRequestSnapshot pr, int maxDiffChars)
    {
        var diff = pr.Diff.Length <= maxDiffChars
            ? pr.Diff
            : pr.Diff[..maxDiffChars] + $"\n... [diff truncated at {maxDiffChars:N0} characters; use read_file for the rest]";

        var sb = new StringBuilder()
            .AppendLine($"Review pull request {pr.RepositoryKey}#{pr.Number} ({pr.BaseRef} <- {pr.HeadRef}) by {pr.Author}.")
            .AppendLine()
            .AppendLine("Changed files:");
        foreach (var f in pr.Files)
            sb.AppendLine($"- {f.Path} ({f.Status}, +{f.Additions}/-{f.Deletions})");

        return sb
            .AppendLine()
            .AppendLine("<untrusted_pull_request_content>")
            .AppendLine($"Title: {pr.Title}")
            .AppendLine("Description:")
            .AppendLine(pr.Description)
            .AppendLine()
            .AppendLine("```diff")
            .AppendLine(diff)
            .AppendLine("```")
            .AppendLine("</untrusted_pull_request_content>")
            .ToString();
    }
}
