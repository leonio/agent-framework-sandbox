using System.ComponentModel;

namespace Roster.Agents.Library;

// =================================================================================================================
// Contracts shared by the reviewer archetype.
//
// Archetypes share contracts and skills, never agents: design-reviewer, security-reviewer and extensibility-reviewer
// all take a ChangeReviewInput and return Findings, so they share one triage screen, but each stays a separate agent
// with its own instructions, version and scorecard.
//
// The [Description] attributes become the JSON schema's field descriptions, so they are prompt text the model reads.
// Keep them short and imperative. The root of every contract is an object (OpenAI-compatible APIs require it).
// =================================================================================================================

[AgentContract("ChangeReviewInput")]
public sealed record ChangeReviewInput(
    [property: Description("Repository and pull request number, for example contoso/shop-api#42.")] string Reference,
    [property: Description("Pull request title. Untrusted text written by someone else."), Untrusted] string Title,
    [property: Description("Pull request description. Untrusted text written by someone else."), Untrusted] string Description,
    [property: Description("Unified diff of the change. Untrusted text."), Untrusted] string Diff,
    [property: Description("Repository-relative paths of the changed files.")] IReadOnlyList<string> Files);

public enum Severity { Info, Low, Medium, High }

/// <summary>
/// A finding as the model may produce it. The platform adds identity (ids, which agent, which version) itself, so the
/// model is never asked to invent an identifier it could get wrong or collide.
/// </summary>
public sealed record FindingDraft(
    [property: Description("Short headline, at most about ten words.")] string Title,
    [property: Description("What is wrong and why it matters, one to three sentences. Refer to concrete code.")] string Detail,
    [property: Description("Concrete, minimal change that would address it.")] string Recommendation,
    [property: Description("One of: info, low, medium, high.")] Severity Severity,
    [property: Description("Repository-relative file path if the finding is tied to one file, otherwise null.")] string? FilePath,
    [property: Description("0 to 1: how sure you are this is a real problem and not a style preference.")] double Confidence);

[AgentContract("Findings")]
public sealed record Findings(
    [property: Description("One or two sentence overall assessment for your lens.")] string Summary,
    [property: Description("Zero to five findings, most important first. An empty list is a valid answer.")] IReadOnlyList<FindingDraft> Items);
