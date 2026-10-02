using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using SdlcStudio.Api.Domain;
using SdlcStudio.Api.Tools;

namespace SdlcStudio.Api.Workflows;

// =============================================================================================
// Testing workflow:  TestingRequest --> [test-designer] --> [publish prompts + Confluence page]
//
// Rather than generating and running tests itself, the studio produces *prompts* a tester pastes
// into their own AI coding assistant inside their checkout. WHY: the tester stays in control of
// their environment and test framework, and the prompts are reusable documentation of intent.
// ALTERNATIVE: add a "test-writer" agent + a step that runs `dotnet test` in the generated repo and
// loops back to the developer on failures (another conditional edge, like the code review loop).
// =============================================================================================

public sealed record TestingRequest(List<StoryInput> Stories, string? Feedback);
public sealed record TestingResult(TestPromptSet Prompts, string Markdown, string ConfluenceUrl);

public static class TestingWorkflow
{
    public static Workflow Build(RunContext run, AIAgent testDesigner, ConfluenceClient confluence)
    {
        var design = new TestDesignerExecutor(run, testDesigner);
        var publish = new TestingPublisherExecutor(run, confluence);
        return new WorkflowBuilder(design).WithName("testing").AddEdge(design, publish).WithOutputFrom(publish).Build();
    }
}

internal sealed class TestDesignerExecutor(RunContext run, AIAgent agent) : Executor<TestingRequest, TestPromptSet>("test-designer")
{
    public override async ValueTask<TestPromptSet> HandleAsync(TestingRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        var stories = string.Join("\n\n", message.Stories.Select(s => s.ToMarkdown()));
        var prompt = run.Prompt("test-prompts", ("stories", stories), ("feedback", message.Feedback));
        return (await agent.RunAsync<TestPromptSet>(prompt, serializerOptions: StudioJson.Options, cancellationToken: ct)).Result;
    }
}

/// <summary>Writes one markdown file per story and publishes a summary page to Confluence (tooling).</summary>
[YieldsOutput(typeof(TestingResult))]
internal sealed class TestingPublisherExecutor(RunContext run, ConfluenceClient confluence) : Executor<TestPromptSet>("publish-test-prompts")
{
    public override async ValueTask HandleAsync(TestPromptSet message, IWorkflowContext context, CancellationToken ct = default)
    {
        Directory.CreateDirectory(run.TestsFolder);
        var sb = new StringBuilder($"# {run.Project}: tester prompts\n");
        foreach (var p in message.Prompts)
        {
            var body = $"# {p.StoryKey}: {p.Title}\n\n```text\n{p.Prompt}\n```\n";
            await File.WriteAllTextAsync(Path.Combine(run.TestsFolder, $"{p.StoryKey}.md"), body, ct);
            sb.AppendLine($"\n## {p.StoryKey}: {p.Title}\n\n```text\n{p.Prompt}\n```");
        }

        var url = await confluence.PublishPageAsync($"{run.Project} - test prompts", sb.ToString(), ct);
        await context.AddEventAsync(new StudioProgressEvent($"Published {message.Prompts.Count} tester prompt(s) to Confluence: {url}"), ct);
        await context.YieldOutputAsync(new TestingResult(message, sb.ToString(), url), ct);
    }
}
