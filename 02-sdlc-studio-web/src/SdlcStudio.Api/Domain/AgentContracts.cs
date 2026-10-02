using System.ComponentModel;
using System.Text.Json;

namespace SdlcStudio.Api.Domain;

// ---------------------------------------------------------------------------------------------
// Structured outputs.
//
// These records are what agents return when we call `agent.RunAsync<T>(...)`. The Agent Framework
// derives a JSON schema from T, sends it to the model as the response format, then deserializes
// the reply into T for us.
//
// WHY structured output instead of asking for markdown and parsing it?
// - Downstream steps (persisting stories, creating Jira tickets, committing files) need data,
//   not prose. A schema-constrained response removes a whole class of parsing bugs.
// - [Description] attributes flow into the JSON schema, so they double as field-level prompts.
//
// WHY are some outputs still plain markdown (specs, test prompts)?
// - Specs are documents meant for humans. Forcing them into JSON makes them worse to read and
//   gains nothing; we store and render them as markdown.
//
// ALTERNATIVE: tool-calling "submit_plan(...)" functions instead of response formats. Useful for
// models that do not support JSON schema output, but noisier to orchestrate.
//
// NOTE: we keep every top-level type an *object* (not a bare array). For non-object roots the
// framework wraps the schema in {"data": ...}; avoiding that keeps prompts and mocks simpler.
// ---------------------------------------------------------------------------------------------

public static class StudioJson
{
    /// <summary>One serializer configuration for structured output, the API and the mock LLM.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

// ----- Review ---------------------------------------------------------------------------------

public sealed record ReviewFindings(
    [property: Description("Findings for the spec, most important first. Return an empty list if there is nothing material.")]
    List<ReviewFindingDraft> Findings);

public sealed record ReviewFindingDraft(
    [property: Description("Short imperative title, e.g. 'Define password reset flow'")] string Title,
    [property: Description("What is missing or wrong and why it matters")] string Detail,
    [property: Description("One of: High, Medium, Low")] string Severity);

// ----- Agile plan -----------------------------------------------------------------------------

public sealed record AgilePlan(
    [property: Description("Sprints in order. Sprint numbers start at 1.")] List<SprintDraft> Sprints,
    [property: Description("Epics that group the user stories")] List<EpicDraft> Epics);

public sealed record SprintDraft(int Number, [property: Description("One sentence sprint goal")] string Goal);

public sealed record EpicDraft(string Key, string Title, string Description, List<StoryDraft> Stories);

public sealed record StoryDraft(
    [property: Description("Unique key, e.g. ST-1")] string Key,
    string Title,
    [property: Description("'As a ... I want ... so that ...'")] string UserStory,
    List<string> AcceptanceCriteria,
    [property: Description("Fibonacci story points: 1, 2, 3, 5 or 8")] int Points,
    int Sprint);

// ----- Dev plan -------------------------------------------------------------------------------

public sealed record TaskBreakdown(
    [property: Description("Small, independently committable tasks in implementation order")] List<DevTaskDraft> Tasks);

public sealed record DevTaskDraft(
    string Title,
    [property: Description("What to build and how to know it is done")] string Detail,
    [property: Description("Relative file paths the task is expected to touch")] List<string> Files);

// ----- Develop --------------------------------------------------------------------------------

public sealed record CodeChange(
    [property: Description("Complete file contents to write. Paths are relative to the repository root.")] List<FileContent> Files,
    [property: Description("Conventional commit message, e.g. 'feat(todo): add item endpoint'")] string CommitMessage,
    [property: Description("Notes for the reviewer")] string Notes);

public sealed record FileContent(string Path, string Content);

public sealed record CodeReview(
    bool Approved,
    [property: Description("Actionable review comments. Empty when approved.")] List<string> Comments);

// ----- Testing --------------------------------------------------------------------------------

public sealed record TestPromptSet(List<TestPrompt> Prompts);

public sealed record TestPrompt(
    string StoryKey,
    string Title,
    [property: Description("A self-contained prompt a tester can paste into their own AI coding assistant")] string Prompt);
