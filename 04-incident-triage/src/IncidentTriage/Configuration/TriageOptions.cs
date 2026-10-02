namespace IncidentTriage.Configuration;

// -------------------------------------------------------------------------------------------------
// Strongly typed configuration.
//
// WHY plain classes with `init`/`set` and no IOptions<T>?
//   This is a console app without a generic host, so we bind once with ConfigurationBinder.Get<T>()
//   and pass the objects around. In an ASP.NET Core / Worker host you would register these with
//   services.AddOptions<AiOptions>().BindConfiguration("Ai").ValidateDataAnnotations().ValidateOnStart()
//   and inject IOptions<AiOptions>. See:
//   https://learn.microsoft.com/dotnet/core/extensions/options
//
// WHY not records?
//   The configuration binder needs settable properties (or a constructor it can match); mutable
//   classes with defaults keep appsettings.json optional for every key.
// -------------------------------------------------------------------------------------------------

/// <summary>Which model backend to use. <see cref="Mock"/> runs fully offline.</summary>
public enum AiProvider
{
    /// <summary>Scripted, deterministic clients (no network, no keys). Default so the sample always runs.</summary>
    Mock,

    /// <summary>Azure OpenAI (or Azure AI Foundry) through its OpenAI-compatible /openai/v1 endpoint.</summary>
    AzureOpenAI,

    /// <summary>api.openai.com, or any OpenAI-compatible endpoint (Ollama, LM Studio, GitHub Models...).</summary>
    OpenAI,
}

public sealed class AiOptions
{
    public AiProvider Provider { get; set; } = AiProvider.Mock;

    /// <summary>Chat model or Azure deployment name.</summary>
    public string ChatModel { get; set; } = "gpt-4.1-mini";

    /// <summary>Embedding model or Azure deployment name used for RAG.</summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Endpoint. For Azure: https://{resource}.openai.azure.com (we append /openai/v1/).
    /// For OpenAI-compatible servers: their base URL. Leave empty for api.openai.com.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// API key. Keep it in user-secrets or an environment variable (Ai__ApiKey), never in appsettings.json.
    /// For Azure, leave empty to use Entra ID (DefaultAzureCredential) instead of a key.
    /// </summary>
    public string? ApiKey { get; set; }
}

public sealed class TriageOptions
{
    /// <summary>Branch used when the user does not give a commit id or tag.</summary>
    public string DefaultRef { get; set; } = "main";

    /// <summary>How many times the user may reject a root-cause hypothesis before we stop asking the model.</summary>
    public int MaxReviewRounds { get; set; } = 3;

    /// <summary>Where run artefacts (journal, ticket, postmortem) are written. Relative to the current directory.</summary>
    public string OutputFolder { get; set; } = "output";

    /// <summary>Where repositories are cloned. Relative to the current directory.</summary>
    public string WorkFolder { get; set; } = ".work";

    /// <summary>How many commits of history to fetch so the "recent changes" tool has something to look at.</summary>
    public int CloneDepth { get; set; } = 30;

    public RagOptions Rag { get; set; } = new();
}

public sealed class RagOptions
{
    /// <summary>Chunks returned per search. Small on purpose: every chunk costs prompt tokens.</summary>
    public int TopK { get; set; } = 4;

    /// <summary>How many whole runbooks/postmortems the root-cause agent gets injected (small-to-big retrieval).</summary>
    public int MaxKnowledgeDocuments { get; set; } = 3;

    /// <summary>Hard cap on files indexed from the repository so a monorepo does not burn the embedding budget.</summary>
    public int MaxFiles { get; set; } = 400;

    /// <summary>Lines per code chunk, and how many lines consecutive chunks overlap.</summary>
    public int CodeChunkLines { get; set; } = 60;
    public int CodeChunkOverlap { get; set; } = 10;

    /// <summary>File extensions treated as source code worth indexing.</summary>
    public string[] CodeExtensions { get; set; } =
        [".cs", ".ts", ".tsx", ".js", ".py", ".go", ".java", ".kt", ".rb", ".json", ".yaml", ".yml", ".sql", ".md"];
}

public sealed class JiraOptions
{
    public string BaseUrl { get; set; } = "https://your-org.atlassian.net";
    public string ProjectKey { get; set; } = "OPS";

    /// <summary>When true (default) the client logs the HTTP request it *would* send and returns a fake key.</summary>
    public bool DryRun { get; set; } = true;
}

public sealed class ConfluenceOptions
{
    public string BaseUrl { get; set; } = "https://your-org.atlassian.net/wiki";
    public string SpaceKey { get; set; } = "ENG";
    public string? ParentPageId { get; set; }
    public bool DryRun { get; set; } = true;
}
