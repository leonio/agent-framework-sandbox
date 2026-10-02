namespace Roster.Platform.Data;

public enum EndpointKind
{
    /// <summary>Anything that speaks the OpenAI API: OpenAI, Azure OpenAI / Foundry, Ollama, vLLM, LiteLLM.</summary>
    OpenAI,

    /// <summary>The offline fake endpoint (models <c>fake</c>, <c>fake-flaky</c>, <c>fake-slow</c>).</summary>
    Fake,

    /// <summary>A Copilot SDK session on the person's own seat. Slice 2.</summary>
    Copilot,
}

/// <summary>
/// A model endpoint someone configured. Endpoints are data, not code: people pick one per assignment in the UI (and,
/// in the advanced panel, per phase or step), and <see cref="ModelRouter"/> turns that choice into a model call.
/// </summary>
public sealed class ModelEndpoint
{
    public Guid Id { get; set; }

    /// <summary>The person who owns it, or null for a shared endpoint that an admin manages and everyone may use.</summary>
    public string? OwnerId { get; set; }

    public required string Name { get; set; }

    public EndpointKind Kind { get; set; }

    /// <summary>Null means the provider's default (api.openai.com for <see cref="EndpointKind.OpenAI"/>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The model used when nothing more specific applies.</summary>
    public required string DefaultModel { get; set; }

    /// <summary>
    /// Which model serves each tier on this endpoint, as JSON: <c>{"fast": "gpt-5-mini", "reasoning": "o4"}</c>. An agent
    /// asks for a tier, not a model; tiers missing here use <see cref="DefaultModel"/>.
    /// </summary>
    public string TierModelsJson { get; set; } = "{}";

    /// <summary>The endpoint enforces a JSON schema. Without it, typed agents use prompted JSON with repair.</summary>
    public bool NativeStructuredOutput { get; set; } = true;

    public bool SupportsTools { get; set; } = true;

    public bool SupportsStreaming { get; set; } = true;

    /// <summary>A reasoning model: returns reasoning content and may reject sampling settings such as temperature.</summary>
    public bool ReasoningModel { get; set; }

    /// <summary>How many calls may be in flight at once from one runner (the per-endpoint gate).</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Recorded for the UI and future global rate limiting; not enforced in slice 1.</summary>
    public int? RequestsPerMinute { get; set; }

    /// <summary>The key to use, owned by <see cref="OwnerId"/> (or, for a shared endpoint, by the admin who set it up).</summary>
    public Guid? CredentialId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
