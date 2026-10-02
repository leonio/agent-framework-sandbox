namespace SdlcStudio.Api.Tools;

/// <summary>
/// Example of consuming an MCP (Model Context Protocol) server, here the GitHub MCP server, so an agent
/// can use GitHub's tools (search code, read files, open pull requests) without us writing an API client.
/// </summary>
/// <remarks>
/// <para>
/// The real implementation is commented out so the sample builds and runs offline with no token.
/// <see cref="GetRepositoryContextAsync"/> returns mocked results in the same shape the MCP tool would.
/// </para>
/// <para>
/// To enable it:
/// <list type="number">
/// <item>Uncomment the ModelContextProtocol package in Directory.Packages.props and the API csproj.</item>
/// <item>Set a token: <c>dotnet user-secrets set "GitHub:Token" "&lt;pat&gt;"</c>.</item>
/// <item>Uncomment the code below and pass <c>await mcp.GetToolsAsync()</c> to <c>AgentFactory.CreateAsync</c>.</item>
/// </list>
/// </para>
/// <para>
/// WHY MCP? Tools become a deployable, shareable component: the same GitHub MCP server works from this app,
/// from VS Code, and from any MCP client, and its tools are discovered at runtime (<c>ListToolsAsync</c>).
/// MCP tools implement <c>AIFunction</c>, so they plug straight into <c>ChatOptions.Tools</c>.
/// TRADE-OFF: you hand the model a large, generic tool surface. Filter the list to the few tools an agent
/// needs (as below) and prefer read-only tools; keep writes (merging, pushing) in deterministic code.
/// </para>
/// </remarks>
public sealed class GitHubMcpTools
{
    /*
    using ModelContextProtocol.Client;
    using Microsoft.Extensions.AI;

    private McpClient? _client;

    public async Task<IList<AITool>> GetToolsAsync(string token, CancellationToken ct = default)
    {
        // Option A: the hosted GitHub MCP server over HTTP.
        _client ??= await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri("https://api.githubcopilot.com/mcp/"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            }),
            cancellationToken: ct);

        // Option B: run the server locally in Docker over stdio.
        // _client ??= await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        // {
        //     Name = "github",
        //     Command = "docker",
        //     Arguments = ["run", "-i", "--rm", "-e", "GITHUB_PERSONAL_ACCESS_TOKEN", "ghcr.io/github/github-mcp-server"],
        //     EnvironmentVariables = new Dictionary<string, string?> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = token },
        // }), cancellationToken: ct);

        var tools = await _client.ListToolsAsync(cancellationToken: ct);

        // Least privilege: only expose the read-only tools this agent needs.
        string[] allowed = ["get_file_contents", "search_code", "list_commits"];
        return [.. tools.Where(t => allowed.Contains(t.Name))];
    }
    */

    /// <summary>
    /// Mocked stand-in for calling the MCP <c>get_file_contents</c> / <c>list_commits</c> tools.
    /// Used by the import phase to show where repository context from GitHub would enter the workflow.
    /// </summary>
    public Task<string> GetRepositoryContextAsync(string repository, CancellationToken ct = default) =>
        Task.FromResult($"""
            (mocked MCP result for {repository})
            Recent commits: "fix: null check in item service", "feat: add due dates", "chore: bump packages"
            Open pull requests: 2. Default branch: main.
            """);
}
