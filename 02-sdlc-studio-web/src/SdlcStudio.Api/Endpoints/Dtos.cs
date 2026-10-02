using System.ComponentModel.DataAnnotations;
using SdlcStudio.Api.Data;
using SdlcStudio.Api.Domain;

namespace SdlcStudio.Api.Endpoints;

// ---------------------------------------------------------------------------------------------
// Request / response contracts for the HTTP API.
//
// WHY DTOs instead of returning EF entities? Entities have navigation cycles, internal fields
// (serialized agent sessions) and change for persistence reasons. DTOs are the stable public shape.
//
// Requests use DataAnnotations; .NET 10 minimal APIs validate them automatically once
// builder.Services.AddValidation() is called (Program.cs), returning a 400 ProblemDetails.
// ALTERNATIVE: FluentValidation with an endpoint filter, for rules that need services or async checks.
// ---------------------------------------------------------------------------------------------

public sealed record StartRunRequest(
    [property: Required, StringLength(200, MinimumLength = 2)] string Name,
    [property: Required, StringLength(4000, MinimumLength = 10)] string Idea);

public sealed record ImportRunRequest(
    [property: Required, StringLength(200, MinimumLength = 2)] string Name,
    [property: Required] string Path);

public sealed record AnswerRequest([property: Required, StringLength(4000, MinimumLength = 1)] string Answer);
public sealed record DecisionRequest([property: StringLength(2000)] string? Reason);
public sealed record FindingDecisionRequest(bool Accepted, [property: StringLength(2000)] string? Reason);

public sealed record TemplateRequest(
    [property: Required, RegularExpression("^[a-z0-9-]+$", ErrorMessage = "Use lower-case letters, digits and dashes.")] string Key,
    TemplateKind Kind,
    [property: Required, StringLength(200)] string Title,
    string? Description,
    [property: Required] string Content,
    string? Skills);

public sealed record TemplateDto(int Id, string Key, TemplateKind Kind, string Title, string Description, string Content, string Skills, int Version, DateTimeOffset UpdatedAt)
{
    public static TemplateDto From(PromptTemplate t) => new(t.Id, t.Key, t.Kind, t.Title, t.Description, t.Content, t.Skills, t.Version, t.UpdatedAt);
}

public sealed record PhaseStepDto(WorkflowPhase Phase, string Name, string State);

public sealed record RunSummaryDto(
    Guid Id, string Name, RunSource Source, WorkflowPhase Phase, string PhaseName, RunStatus Status, string? StatusMessage,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static RunSummaryDto From(WorkflowRun r) =>
        new(r.Id, r.Name, r.Source, r.Phase, r.Phase.DisplayName, r.Status, r.StatusMessage, r.CreatedAt, r.UpdatedAt);
}

public sealed record RunDetailDto(
    RunSummaryDto Run,
    string WorkspacePath,
    string? SourcePath,
    int ReviewRound,
    List<PhaseStepDto> Pipeline,
    List<MessageDto> Messages,
    List<ArtifactDto> Artifacts,
    List<FindingDto> Findings,
    List<EpicDto> Epics,
    List<CheckpointDto> Checkpoints,
    List<DecisionDto> Decisions,
    List<EventDto> Events);

public sealed record MessageDto(long Id, string Role, string Content, DateTimeOffset CreatedAt);
public sealed record ArtifactDto(long Id, WorkflowPhase Phase, string Kind, string Title, string Content, string? RelativePath, int Version, DateTimeOffset CreatedAt);
public sealed record FindingDto(long Id, int Round, string Reviewer, string Severity, string Title, string Detail, FindingStatus Status, string? DecisionReason);
public sealed record EpicDto(int Id, string Key, string Title, string Description, string? ExternalId, List<StoryDto> Stories);
public sealed record StoryDto(int Id, string Key, string Title, string UserStory, List<string> AcceptanceCriteria, int Points, int Sprint, WorkItemStatus Status, string? ExternalId, List<TaskDto> Tasks);
public sealed record TaskDto(int Id, int Order, string Title, string Detail, string Files, WorkItemStatus Status, string? CommitSha);
public sealed record CheckpointDto(long Id, int? StoryId, CheckpointKind Kind, string Title, string Detail, string? Branch, string? Sha, DateTimeOffset CreatedAt);
public sealed record DecisionDto(long Id, WorkflowPhase Phase, bool Approved, string Reason, DateTimeOffset CreatedAt);
public sealed record EventDto(long Id, WorkflowPhase Phase, string Level, string Message, DateTimeOffset CreatedAt);

public sealed record MetaDto(string LlmProvider, string Model, string IntegrationsMode, string Database, IReadOnlyList<PhaseInfoDto> Phases);
public sealed record PhaseInfoDto(WorkflowPhase Phase, string Name, bool HasApprovalGate);
