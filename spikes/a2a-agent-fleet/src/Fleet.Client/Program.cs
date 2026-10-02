using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using Json.Schema;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;

// THE PLATFORM SIDE of the fleet.
//
// It has no reference to any team's code. For each registered agent it:
//   1. resolves the A2A agent card (standard discovery),
//   2. follows the card's extension to the published contract document and checks the hash matches the card,
//   3. refuses an agent whose contract hash differs from the hash we pinned,
//   4. validates the input against the published input schema before sending anything,
//   5. calls the agent over A2A,
//   6. validates the answer against the published output schema,
//   7. writes a ledger-style row (the real platform writes these for every invocation, local or remote).

const string ContractExtension = "https://roster.dev/a2a/ext/agent-contract/v0";

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
var prettyJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

using var tracing = new ActivityListener
{
    ShouldListenTo = _ => true,
    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
};
ActivitySource.AddActivityListener(tracing);
var source = new ActivitySource("Fleet.Client");

string baseDir = AppContext.BaseDirectory;
List<FleetEntry> fleet = JsonSerializer.Deserialize<List<FleetEntry>>(File.ReadAllText(Path.Combine(baseDir, "fleet.json")), json) ?? [];
string inputText = File.ReadAllText(Path.Combine(baseDir, "sample-input.json"));
JsonElement inputElement = JsonDocument.Parse(inputText).RootElement.Clone();

async Task<Registered?> RegisterAsync(FleetEntry entry)
{
    string token = Environment.GetEnvironmentVariable(entry.TokenEnv) ?? "dev-token";
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    try
    {
        // 1. standard A2A discovery
        AgentCard card = await new A2ACardResolver(new Uri(entry.BaseUrl), http).GetAgentCardAsync();

        // 2. the card points at the contract through an extension
        AgentExtension? ext = card.Capabilities?.Extensions?.FirstOrDefault(e => e.Uri == ContractExtension);
        if (ext?.Params is not JsonElement extParams
            || !extParams.TryGetProperty("contractUrl", out JsonElement contractUrl)
            || !extParams.TryGetProperty("hash", out JsonElement cardHash))
        {
            Console.WriteLine($"  REFUSED {entry.Name}: the card does not advertise a contract extension.");
            return null;
        }

        string contractText = await http.GetStringAsync(contractUrl.GetString()!);
        JsonNode contract = JsonNode.Parse(contractText)!;
        string hash = contract["hash"]!.GetValue<string>();

        if (hash != cardHash.GetString())
        {
            Console.WriteLine($"  REFUSED {entry.Name}: contract hash {Short(hash)} does not match the card's {Short(cardHash.GetString()!)}");
            return null;
        }

        // 3. a platform pins the hash it registered; a changed contract is a breaking change until someone re-registers
        if (entry.PinnedHash is not null && entry.PinnedHash != hash)
        {
            Console.WriteLine($"  REFUSED {entry.Name}: pinned {Short(entry.PinnedHash)} but the agent now publishes {Short(hash)} (breaking change)");
            return null;
        }

        JsonSchema inputSchema = JsonSchema.FromText(contract["input"]!["schema"]!.ToJsonString());
        JsonSchema outputSchema = JsonSchema.FromText(contract["output"]!["schema"]!.ToJsonString());
        Console.WriteLine($"  OK      {contract["name"]} v{contract["version"]}  owner={contract["owner"]}  runtime={contract["runtime"]}  risk=[{string.Join(",", contract["risk"]!.AsArray())}]  hash={Short(hash)}");
        return new Registered(entry, card, contract, hash, inputSchema, outputSchema);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAILED  {entry.Name}: {ex.GetType().Name}: {ex.Message}");
        return null;
    }
}

static string Short(string hash) => hash.Length <= 18 ? hash : hash[..18] + "...";

Console.WriteLine("== 1. Register: discover each agent and read its contract ==");
var registry = new List<Registered>();
foreach (FleetEntry entry in fleet)
{
    if (await RegisterAsync(entry) is { } registered)
    {
        registry.Add(registered);
    }
}

Console.WriteLine();
Console.WriteLine("== 2. Invoke each agent over A2A ==");
var ledger = new List<LedgerRow>();
foreach (Registered reg in registry)
{
    string name = reg.Contract["name"]!.GetValue<string>();
    string iface = reg.Card.SupportedInterfaces![0].Url;

    // 4. validate the input against the schema the team published, before sending anything
    EvaluationResults inputCheck = reg.InputSchema.Evaluate(inputElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
    if (!inputCheck.IsValid)
    {
        Console.WriteLine($"  {name}: input does not satisfy the published schema, not sent.");
        continue;
    }

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable(reg.Entry.TokenEnv) ?? "dev-token");

    using Activity? activity = source.StartActivity($"invoke {name}", ActivityKind.Client);
    string traceId = activity?.TraceId.ToString() ?? "none";
    var clock = Stopwatch.StartNew();
    string outcome;
    string? detail = null;
    int? findingCount = null;
    string? contextId = null, taskId = null;

    try
    {
        IA2AClient a2a = A2AClientFactory.Create(reg.Card, http, new A2AClientOptions { PreferredBindings = [ProtocolBindingNames.HttpJson] });
        var agent = new A2AAgent(a2a, name: name, description: reg.Card.Description);
        AgentSession session = await agent.CreateSessionAsync();

        // 5. the typed input travels as JSON text; the contract says application/json
        AgentResponse response = await agent.RunAsync(inputText, session);
        if (session is A2AAgentSession a2aSession)
        {
            contextId = a2aSession.ContextId;
            taskId = a2aSession.TaskId;
        }

        // 6. validate the answer against the schema the team published
        string text = response.Text;
        JsonNode? answer = null;
        JsonElement answerElement = default;
        try
        {
            answer = JsonNode.Parse(text);
            answerElement = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            answer = null;
        }

        if (answer is null)
        {
            outcome = "not-json";
            detail = text.Length > 120 ? text[..120] : text;
        }
        else
        {
            EvaluationResults outputCheck = reg.OutputSchema.Evaluate(answerElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            outcome = outputCheck.IsValid ? "ok" : "schema-violation";
            findingCount = answer["items"]?.AsArray().Count;
            if (outcome == "ok")
            {
                foreach (JsonNode? f in answer["items"]!.AsArray())
                {
                    Console.WriteLine($"      [{f!["severity"]}] {f["title"]}  ({f["filePath"]})");
                }
            }
        }
    }
    catch (Exception ex)
    {
        outcome = "error";
        detail = $"{ex.GetType().Name}: {ex.Message}";
    }

    clock.Stop();
    var row = new LedgerRow(
        DateTimeOffset.UtcNow, name, reg.Contract["owner"]!.GetValue<string>(), reg.Contract["runtime"]!.GetValue<string>(),
        reg.Hash, iface, clock.ElapsedMilliseconds, outcome, findingCount, contextId, taskId, traceId, detail);
    ledger.Add(row);
    Console.WriteLine($"  {name,-18} outcome={outcome,-16} {clock.ElapsedMilliseconds,5} ms  findings={findingCount}  task={(taskId is null ? "(message)" : taskId[..Math.Min(8, taskId.Length)])}  trace={traceId[..8]}");
}

Console.WriteLine();
Console.WriteLine("== 3. Negative checks ==");
if (registry.Count > 0)
{
    // Without a token the A2A endpoint must refuse. The framework does not secure these endpoints; the host has to.
    Registered first = registry[0];
    using var anon = new HttpClient();
    HttpResponseMessage unauth = await anon.PostAsync(first.Card.SupportedInterfaces![0].Url + "/message:send",
        new StringContent("{}", Encoding.UTF8, "application/json"));
    Console.WriteLine($"  no token       -> {(int)unauth.StatusCode} {unauth.StatusCode}   (expected 401)");

    // A caller that pinned an older contract hash refuses the agent once its contract changes. Simulated by pinning a
    // hash the agent does not publish.
    Console.WriteLine("  stale pin (the agent now publishes a different contract hash):");
    Registered? stale = await RegisterAsync(fleet[0] with { PinnedHash = "sha256:0000000000000000000000000000000000000000" });
    Console.WriteLine(stale is null ? "    -> refused, as expected" : "    -> UNEXPECTEDLY accepted");
}

string ledgerPath = Path.Combine(Directory.GetCurrentDirectory(), "ledger.jsonl");
await File.WriteAllLinesAsync(ledgerPath, ledger.Select(r => JsonSerializer.Serialize(r, json)));
Console.WriteLine();
Console.WriteLine($"== 4. Ledger rows written to {ledgerPath} ==");
foreach (LedgerRow row in ledger)
{
    Console.WriteLine(JsonSerializer.Serialize(row, prettyJson));
}

internal sealed record FleetEntry(string Name, string BaseUrl, string TokenEnv, string? PinnedHash = null);

internal sealed record Registered(FleetEntry Entry, AgentCard Card, JsonNode Contract, string Hash, JsonSchema InputSchema, JsonSchema OutputSchema);

internal sealed record LedgerRow(
    DateTimeOffset At, string Agent, string Owner, string Runtime, string ContractHash, string Interface,
    long Ms, string Outcome, int? Findings, string? ContextId, string? TaskId, string TraceId, string? Detail);
