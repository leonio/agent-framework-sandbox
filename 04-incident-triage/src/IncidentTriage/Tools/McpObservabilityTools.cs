using System.ComponentModel;
using Microsoft.Extensions.AI;
// using ModelContextProtocol.Client;   // <- needed by the commented-out MCP example below

namespace IncidentTriage.Tools;

/// <summary>
/// Observability tools for the root-cause agent: "show me the logs for service X around time T".
/// </summary>
/// <remarks>
/// <para><b>This is the sample's MCP example, intentionally commented out and mocked.</b></para>
/// <para>In a real setup you would not write a Loki/Kusto/Datadog client yourself. You would connect to an
/// MCP (Model Context Protocol) server that already wraps your observability stack, and hand its tools to
/// the agent. Every MCP tool surfaces in .NET as an <c>McpClientTool</c>, which <i>is</i> an
/// <see cref="AIFunction"/>, so it drops into <c>ChatOptions.Tools</c> next to our own C# tools with no
/// adapter code. Candidates:</para>
/// <list type="bullet">
///   <item>Grafana MCP server (Loki logs, Prometheus metrics, dashboards): https://github.com/grafana/mcp-grafana</item>
///   <item>Azure MCP Server (Log Analytics / App Insights KQL via <c>monitor</c> tools): https://github.com/microsoft/mcp</item>
///   <item>Datadog, Sentry and PagerDuty all publish MCP servers too.</item>
/// </list>
/// <para><b>WHY mock it here?</b> The sample must run offline, and an MCP server needs its own process,
/// credentials and a backend with data in it. The mocked <c>query_logs</c> below has the same shape an MCP
/// tool would have (name, description, JSON arguments, text result), so swapping is a one-line change in
/// <c>AgentFactory</c>: replace <c>McpObservabilityTools.Mocked()</c> with <c>await ConnectAsync()</c>.</para>
/// <para><b>Things to decide before enabling MCP for real:</b> which tools to expose (filter the list; a
/// Grafana server also has "create dashboard"), whether tool calls need approval
/// (<c>ApprovalRequiredAIFunction</c>), and that tool <i>results</i> are untrusted input: a log line can
/// contain "ignore previous instructions" just as easily as a pasted report can.</para>
/// </remarks>
public static class McpObservabilityTools
{
    /*
    // ---- Real MCP client (ModelContextProtocol 2.x) ------------------------------------------------
    // Option A: a local stdio server, e.g. Grafana's MCP server binary/container.
    public static async Task<(McpClient Client, IList<AITool> Tools)> ConnectAsync(CancellationToken ct = default)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "grafana",
            Command = "docker",
            Arguments = ["run", "--rm", "-i", "-e", "GRAFANA_URL", "-e", "GRAFANA_SERVICE_ACCOUNT_TOKEN", "mcp/grafana", "-t", "stdio"],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["GRAFANA_URL"] = Environment.GetEnvironmentVariable("GRAFANA_URL"),
                ["GRAFANA_SERVICE_ACCOUNT_TOKEN"] = Environment.GetEnvironmentVariable("GRAFANA_SERVICE_ACCOUNT_TOKEN"),
            },
        });

        // Option B: a remote server over Streamable HTTP.
        // var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("https://mcp.example.com/mcp") });

        var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        // Least privilege: only hand the agent read-only query tools, not everything the server offers.
        string[] allowed = ["query_loki_logs", "query_prometheus", "list_datasources"];
        var tools = (await client.ListToolsAsync(cancellationToken: ct))
            .Where(t => allowed.Contains(t.Name))
            .Cast<AITool>()
            .ToList();

        // The caller owns `client` (IAsyncDisposable) for the lifetime of the run: disposing it stops the server process.
        return (client, tools);
    }
    */

    /// <summary>The offline stand-in: same contract as an MCP log-query tool, canned results.</summary>
    public static IEnumerable<AITool> Mocked() => [AIFunctionFactory.Create(QueryLogs, "query_logs")];

    [Description("Query recent application logs for a service (mocked MCP tool). Returns up to 10 log lines, newest last.")]
    private static string QueryLogs(
        [Description("Service name, e.g. 'checkout-api'.")] string service,
        [Description("Optional text the log line must contain, e.g. an exception name.")] string? contains = null)
    {
        // Canned log lines keyed by service, standing in for what Loki / Log Analytics would return.
        string[] lines = service switch
        {
            "checkout-api" =>
            [
                "2026-09-30T14:02:11Z INFO  checkout-api deploy 4.18.0 started (commit 9f3c2ab)",
                "2026-09-30T14:09:47Z WARN  checkout-api OrderRepository.SaveLineItemAsync took 4.8s",
                "2026-09-30T14:11:02Z ERROR checkout-api System.InvalidOperationException: Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool.",
                "2026-09-30T14:11:03Z ERROR checkout-api POST /api/checkout -> 503 (upstream: sql-orders)",
                "2026-09-30T14:12:30Z WARN  checkout-api SqlConnection pool: 100/100 in use, 37 waiters",
            ],
            "search-api" =>
            [
                "2026-09-30T13:58:00Z INFO  search-api catalogue reindex job started",
                "2026-09-30T13:58:04Z WARN  search-api ProductCache hit ratio dropped to 0.08",
                "2026-09-30T13:58:20Z WARN  search-api p99 latency 2.9s (SLO 800ms)",
            ],
            _ => [$"(no logs found for '{service}')"],
        };

        return string.Join('\n', lines.Where(l => contains is null || l.Contains(contains, StringComparison.OrdinalIgnoreCase)).TakeLast(10));
    }
}
