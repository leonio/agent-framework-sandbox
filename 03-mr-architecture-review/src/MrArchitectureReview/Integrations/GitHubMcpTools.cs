using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrArchitectureReview.Configuration;

// Uncomment together with the method below:
// using ModelContextProtocol.Client;

namespace MrArchitectureReview.Integrations;

/// <summary>
/// GitHub write operations for the publisher agent, via the <b>Model Context Protocol</b> (MCP).
/// </summary>
/// <remarks>
/// <para>
/// <b>What MCP gives you.</b> An MCP server publishes a catalogue of tools (name, description, JSON
/// schema). The C# MCP client turns each one into an <see cref="AIFunction"/>, so an agent can call
/// them exactly like our own C# tools. No hand-written REST client, and the server's maintainers
/// keep the tool descriptions model-friendly. GitHub's official server exposes ~100 tools
/// (issues, PRs, reviews, code search, actions...).
/// </para>
/// <para>
/// <b>Why MCP here but plain REST for fetching the PR?</b> Fetching is fixed plumbing (code is
/// better). Publishing is where the <i>agent</i> decides what to say and which tools to use, and
/// where a growing catalogue of operations is useful. Also: an MCP server can be swapped (GitHub ->
/// GitLab MCP) without touching the agent.
/// </para>
/// <para>
/// <b>Why is the real code commented out?</b> It needs Docker (or a remote server URL) and a GitHub
/// token with write access to the repo, and running it would post real comments. The active code
/// below is a <i>mock with the same tool name and parameters</i> as the real server's
/// <c>add_issue_comment</c> (PR conversation comments are issue comments in GitHub's API), so the
/// publisher prompt and agent work unchanged when you switch.
/// </para>
/// <para>
/// <b>Security notes for real use:</b> give the agent only the tools it needs (filter the list as
/// shown); use a fine-grained token scoped to one repository; and consider wrapping write tools in
/// <c>ApprovalRequiredAIFunction</c> so a human approves each call. Remote MCP servers are
/// third-party code paths that see your data, so treat adding one like adding a dependency.
/// </para>
/// </remarks>
public sealed class GitHubMcpTools(IOptions<ReviewOptions> options, ILogger<GitHubMcpTools> logger)
{
    /// <summary>Tool names the publisher is allowed to use from the GitHub MCP server.</summary>
    private static readonly HashSet<string> s_allowed = ["add_issue_comment"];

    public Task<IList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------------------------------
        // REAL MCP (uncomment, plus the `using ModelContextProtocol.Client;` at the top).
        // Option A - local server in Docker over stdio:
        //
        // var transport = new StdioClientTransport(new StdioClientTransportOptions
        // {
        //     Name = "github",
        //     Command = "docker",
        //     Arguments = ["run", "-i", "--rm", "-e", "GITHUB_PERSONAL_ACCESS_TOKEN", "ghcr.io/github/github-mcp-server"],
        //     EnvironmentVariables = new Dictionary<string, string?>
        //     {
        //         ["GITHUB_PERSONAL_ACCESS_TOKEN"] = options.Value.GitHub.Token,
        //     },
        // });
        //
        // Option B - GitHub's hosted server over streamable HTTP (no Docker):
        //
        // var transport = new HttpClientTransport(new HttpClientTransportOptions
        // {
        //     Endpoint = new Uri("https://api.githubcopilot.com/mcp/"),
        //     AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {options.Value.GitHub.Token}" },
        // });
        //
        // NOTE: the client owns a process/connection. In real code keep it alive for the app's lifetime
        // (register as a singleton and dispose on shutdown) rather than per call as this sketch implies.
        // var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        // IList<McpClientTool> all = await client.ListToolsAsync(cancellationToken: cancellationToken);
        // return [.. all.Where(t => s_allowed.Contains(t.Name))];   // McpClientTool : AIFunction
        // ------------------------------------------------------------------------------------------

        _ = s_allowed; // referenced by the commented code above
        logger.LogDebug("Using mock GitHub MCP tools (GitHub token configured: {HasToken})",
            !string.IsNullOrWhiteSpace(options.Value.GitHub.Token));

        IList<AITool> mock = [AIFunctionFactory.Create(AddIssueComment, "add_issue_comment")];
        return Task.FromResult(mock);
    }

    // Same name and parameter names as the GitHub MCP server's tool, so prompts are portable.
    [Description("Add a comment to a GitHub issue or pull request conversation.")]
    private string AddIssueComment(
        [Description("Repository owner")] string owner,
        [Description("Repository name")] string repo,
        [Description("Issue or pull request number")] int issue_number,
        [Description("Comment body (Markdown)")] string body)
    {
        logger.LogInformation("[mcp-mock] github.add_issue_comment {Owner}/{Repo}#{Number}\n{Body}", owner, repo, issue_number, body);
        return $$"""{"id": 1, "html_url": "https://github.com/{{owner}}/{{repo}}/pull/{{issue_number}}#issuecomment-mock"}""";
    }
}
