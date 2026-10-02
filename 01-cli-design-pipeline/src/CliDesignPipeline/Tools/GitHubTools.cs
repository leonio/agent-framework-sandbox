using System.ComponentModel;
using System.Text.Json;
using CliDesignPipeline.Configuration;
using CliDesignPipeline.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#if ENABLE_MCP
using ModelContextProtocol.Client;
#endif

namespace CliDesignPipeline.Tools;

/// <summary>
/// Supplies the "open a pull request" tool to the release agent - either from GitHub's MCP
/// server, or (by default) a local mock with the same name and shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>What MCP buys you.</b> The Model Context Protocol lets a server publish tools (with
/// schemas) that any MCP-capable client can discover at runtime. The C# SDK's
/// <c>McpClientTool</c> <i>is</i> an <see cref="AIFunction"/>, so MCP tools drop straight into an
/// agent's tool list next to our own C# functions - no adapter code. You get GitHub's
/// officially-maintained tools (PRs, issues, code search, Actions...) without writing a client.
/// </para>
/// <para>
/// <b>What it costs.</b> A network hop per call, another credential to manage, and a tool surface
/// you do not control (GitHub's server exposes ~100 tools; dumping all of them into the prompt
/// wastes tokens and confuses smaller models). So we <b>filter to the one tool we need</b>.
/// </para>
/// <para>
/// <b>Switching it on.</b> The real implementation below is compiled only with
/// <c>dotnet run -p:EnableMcp=true</c> and needs <c>Integrations:GitHub:Token</c> (a fine-grained
/// PAT with pull-request write on the target repo). Everything else in the pipeline is unchanged,
/// which is the point of programming against <see cref="AITool"/>.
/// </para>
/// </remarks>
public sealed class GitHubTools(IOptions<IntegrationOptions> options, IOptions<PipelineOptions> pipeline, ILoggerFactory loggerFactory)
{
    public async Task<IList<AITool>> GetToolsAsync(CancellationToken ct = default)
    {
#if ENABLE_MCP
        var gh = options.Value.GitHub;
        // Remote (Streamable HTTP) MCP server hosted by GitHub. The local alternative is the
        // stdio transport launching the server as a child process:
        //   new StdioClientTransport(new() { Name = "github", Command = "docker",
        //       Arguments = ["run", "-i", "--rm", "-e", "GITHUB_PERSONAL_ACCESS_TOKEN", "ghcr.io/github/github-mcp-server"],
        //       EnvironmentVariables = { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = gh.Token } }, loggerFactory);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "github",
            Endpoint = gh.McpEndpoint,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {gh.Token}" },
        }, loggerFactory);

        // NOTE: the client owns a live connection. In a long-running host register it as a
        // singleton and dispose it on shutdown; for a one-shot CLI process exit is fine.
        var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return [.. tools.Where(t => t.Name == "create_pull_request")];
#else
        _ = loggerFactory;
        await Task.CompletedTask;
        return [AIFunctionFactory.Create(CreatePullRequestMock, "create_pull_request")];
#endif
    }

    /// <summary>
    /// Mirrors the parameters of the GitHub MCP server's <c>create_pull_request</c> tool so the
    /// prompt and the model's behaviour are identical with or without MCP.
    /// </summary>
    [Description("Create a pull request in a GitHub repository. (MOCK: nothing is sent to GitHub.)")]
    private string CreatePullRequestMock(
        [Description("Branch containing the changes.")] string head,
        [Description("Branch to merge into, usually 'main'.")] string @base,
        [Description("Pull request title.")] string title,
        [Description("Pull request body, Markdown.")] string body,
        [Description("Open as draft.")] bool draft = true)
    {
        var gh = options.Value.GitHub;
        // Shaped like the real tool's result so downstream parsing is the same.
        return JsonSerializer.Serialize(new
        {
            number = 42,
            html_url = $"https://github.com/{gh.Owner}/{gh.Repo}/pull/42",
            state = "open",
            draft,
            head = new { @ref = head },
            @base = new { @ref = @base },
            title,
            body_preview = body.Length > 120 ? body[..120] + "..." : body,
            mocked = true,
            work_item = pipeline.Value.WorkItemKey,
        }, PipelineJson.Options);
    }
}
