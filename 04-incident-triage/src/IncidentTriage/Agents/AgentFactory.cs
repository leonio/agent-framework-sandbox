using IncidentTriage.Ai;
using IncidentTriage.Persistence;
using IncidentTriage.Prompting;
using IncidentTriage.Rag;
using IncidentTriage.Repo;
using IncidentTriage.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace IncidentTriage.Agents;

/// <summary>
/// Builds the five small agents the workflow orchestrates. Each one has one job, one prompt file and
/// only the tools and context that job needs.
/// </summary>
/// <remarks>
/// <para><b>WHY several small agents rather than one big "incident bot"?</b></para>
/// <list type="bullet">
///   <item>Each prompt stays short and testable; you can evaluate the signal extractor alone.</item>
///   <item>Least privilege: only the jira-drafter can search Jira; only the root-cause analyst can read code.</item>
///   <item>Cost: cheap steps (extraction) can use a small model and expensive steps a reasoning model.</item>
///   <item>The <i>workflow</i>, not the model, decides the order of steps and where humans step in, which is
///     what makes the process auditable.</item>
/// </list>
/// <para><b>Alternatives in Agent Framework:</b> <c>AgentWorkflowBuilder.BuildSequential/BuildConcurrent</c>
/// for simple pipelines of agents; <c>HandoffsWorkflowBuilder</c> when agents should decide who goes next;
/// <c>MagenticWorkflowBuilder</c> for a planner/manager agent that delegates dynamically; or
/// <c>agent.AsAIFunction()</c> to expose one agent as a tool of another ("agent as tool").
/// We use a hand-built graph because triage has a fixed shape with human gates in known places.</para>
/// </remarks>
public sealed class AgentFactory(ModelClients models, PromptLibrary prompts, KnowledgeIndex index, JiraClient jira, TriageJournal journal)
{
    public AIAgent CreateSignalExtractor() =>
        Build(AgentNames.SignalExtractor, skills: ["severity-rubric", "log-forensics"]);

    public AIAgent CreateIncidentCorrelator() =>
        Build(AgentNames.IncidentCorrelator);

    /// <summary>
    /// The root-cause analyst gets BOTH kinds of RAG plus tools:
    /// runbooks/postmortems pushed in automatically, code pulled on demand, git history, and logs (mocked MCP).
    /// </summary>
    public AIAgent CreateRootCauseAnalyst(RepoSnapshot snapshot)
    {
        var codeTools = new CodeTools(index, snapshot, Path.Combine(prompts.SampleDataFolder, "demo-commits.txt"));

        // ---- RAG, "push" style: Agent Framework's built-in TextSearchProvider -------------------------
        // Before every model call it takes the incoming messages, runs our search delegate, and injects the
        // hits as extra context. The agent code never has to remember to "do RAG".
        // OnDemandFunctionCalling instead would expose the same search as a tool (that's what we do for code).
        var knowledgeRag = new TextSearchProvider(
            index.SearchKnowledgeForProviderAsync,
            new TextSearchProviderOptions
            {
                SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,
                // Our own formatter: a stable "[kind:file] title" header per hit, which the prompt tells the
                // model to use as the citation. Consistent citations are what make answers checkable.
                ContextFormatter = results =>
                    "Relevant runbook and postmortem excerpts (cite as [kind:file]):\n\n" +
                    string.Join("\n\n", results.Select(r => $"[{r.SourceName}] {r.Text}")),
            });

        return Build(
            AgentNames.RootCauseAnalyst,
            tools: [.. codeTools.AsAITools(), .. McpObservabilityTools.Mocked()],
            contextProviders: [knowledgeRag],
            skills: ["log-forensics"]);
    }

    public AIAgent CreateJiraDrafter() =>
        Build(AgentNames.JiraDrafter, tools: [.. jira.AsReadOnlyAITools()]);

    public AIAgent CreatePostmortemWriter() =>
        Build(AgentNames.PostmortemWriter, skills: ["postmortem-blameless"]);

    private AIAgent Build(string name, IList<AITool>? tools = null, IReadOnlyList<AIContextProvider>? contextProviders = null, string[]? skills = null)
    {
        var prompt = prompts.Load(name);

        List<AIContextProvider> providers = [.. contextProviders ?? []];
        if (skills is { Length: > 0 })
        {
            // ---- Skills: the Agent Skills format (Skills/<name>/SKILL.md) -------------------------------
            // Progressive disclosure: only each skill's name + description go into the system prompt; the
            // model calls the load_skill tool to pull the full body when it decides it needs it. Prompts are
            // always-on context, skills are pay-per-use context. Spec: https://agentskills.io
            // We filter per agent so the jira-drafter is not even told the log-forensics skill exists.
            providers.Add(new AgentSkillsProviderBuilder()
                // File skills may ship scripts (scripts/*.py, *.sh). Ours don't, and executing model-chosen
                // scripts is a sandboxing decision you should make deliberately, so the runner refuses.
                // To allow scripts, run them in a container / sandbox here and keep script approval enabled.
                .UseFileSkill(prompts.SkillsFolder, scriptRunner: (skill, script, _, _, _) =>
                    Task.FromResult<object?>($"Script execution is disabled in this sample (skill '{skill.Frontmatter.Name}')."))
                .UseFilter((skill, _) => skills.Contains(skill.Frontmatter.Name))
                .UseOptions(o =>
                {
                    // Skills here are read-only markdown, so loading them needs no human approval. If a skill
                    // shipped scripts, keep DisableRunSkillScriptApproval = false and handle the approval request.
                    o.DisableLoadSkillApproval = true;
                    o.DisableReadSkillResourceApproval = true;
                })
                .Build());
        }

        var agent = models.CreateChatClient(name).AsAIAgent(new ChatClientAgentOptions
        {
            Name = prompt.Name,
            Description = prompt.Description,
            ChatOptions = new ChatOptions
            {
                Instructions = prompt.Instructions,
                Tools = tools,
                // Temperature is deliberately not set: reasoning models (o-series, gpt-5) reject it, and the
                // structured-output schema already constrains the shape. Set ~0.2 for classic chat models.
            },
            AIContextProviders = providers,
            // When the model asks for several tools in one turn (search_code + recent_commits), run them in
            // parallel. Safe here because every tool is read-only and thread-safe.
            AllowConcurrentInvocation = true,
        });

        // ---- Middleware: observe every tool call ---------------------------------------------------------
        // AIAgentBuilder.Use(...) wraps the agent in a function-invocation middleware. Same idea as ASP.NET
        // Core middleware: log, time, block, or rewrite a call; here we print it and journal it.
        // Alternatives: OpenTelemetry (.UseOpenTelemetry()) for traces, or ApprovalRequiredAIFunction to gate a tool.
        return agent.AsBuilder()
            .Use(async (callingAgent, context, next, ct) =>
            {
                var args = string.Join(", ", context.Arguments.Select(a => $"{a.Key}={Shorten(a.Value?.ToString())}"));
                ConsoleUi.Tool(callingAgent.Name ?? name, $"{context.Function.Name}({args})");
                var result = await next(context, ct);
                journal.Record(JournalKind.ToolCall, callingAgent.Name ?? name, context.Function.Name,
                    new { Arguments = context.Arguments.ToDictionary(a => a.Key, a => a.Value?.ToString()), Result = Shorten(result?.ToString(), 600) });
                return result;
            })
            .Build();
    }

    private static string Shorten(string? s, int max = 60) => s is null ? "null" : s.Length <= max ? s : s[..max] + "…";
}
