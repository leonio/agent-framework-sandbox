namespace SdlcStudio.Api.Options;

// Strongly typed configuration, bound with the Options pattern in Program.cs.
// WHY: one place documents every setting, and services take IOptions<T> instead of reading
// raw strings from IConfiguration all over the code base.

public enum LlmProvider
{
    /// <summary>Offline scripted responses. The whole app works end to end with no keys.</summary>
    Mock,
    /// <summary>api.openai.com (set Llm:ApiKey).</summary>
    OpenAI,
    /// <summary>Azure OpenAI / Azure AI Foundry via its OpenAI-compatible v1 endpoint.</summary>
    AzureOpenAI,
    /// <summary>GitHub Models (Endpoint https://models.github.ai/inference, ApiKey = a GitHub PAT).</summary>
    GitHubModels,
}

public sealed class LlmOptions
{
    public const string Section = "Llm";
    public LlmProvider Provider { get; set; } = LlmProvider.Mock;
    public string Model { get; set; } = "gpt-4.1-mini";
    public string Endpoint { get; set; } = "";
    /// <summary>Use user-secrets or environment variables (Llm__ApiKey). Never commit keys.</summary>
    public string ApiKey { get; set; } = "";
}

public sealed class StudioOptions
{
    public const string Section = "Studio";
    public string WorkspaceRoot { get; set; } = "workspace";
    public int MaxConcurrentJobs { get; set; } = 3;
    public GitOptions Git { get; set; } = new();
    public IntegrationOptions Integrations { get; set; } = new();
}

public sealed class GitOptions
{
    public bool Enabled { get; set; } = true;
    public string AuthorName { get; set; } = "SDLC Studio Agent";
    public string AuthorEmail { get; set; } = "agent@sdlc-studio.local";
    public string Remote { get; set; } = "https://github.com/your-org/your-repo.git";
}

public enum IntegrationMode { Mock, Live }

public sealed class IntegrationOptions
{
    public IntegrationMode Mode { get; set; } = IntegrationMode.Mock;
    public string JiraBaseUrl { get; set; } = "";
    public string JiraProjectKey { get; set; } = "STUDIO";
    public string ConfluenceBaseUrl { get; set; } = "";
    public string ConfluenceSpaceKey { get; set; } = "ENG";
    public string AtlassianEmail { get; set; } = "";
    public string AtlassianApiToken { get; set; } = "";
}
