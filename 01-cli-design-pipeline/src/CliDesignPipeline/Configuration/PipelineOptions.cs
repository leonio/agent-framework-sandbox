namespace CliDesignPipeline.Configuration;

/// <summary>Which model backend the agents talk to.</summary>
public enum ModelProvider
{
    /// <summary>Scripted, offline <see cref="Microsoft.Extensions.AI.IChatClient"/>. No keys needed.</summary>
    Mock,
    OpenAI,
    /// <summary>Azure OpenAI through its OpenAI-compatible <c>/openai/v1/</c> endpoint.</summary>
    AzureOpenAI,
    /// <summary>GitHub Models (<c>https://models.github.ai/inference</c>), authenticated with a PAT.</summary>
    GitHubModels,
}

/// <summary>
/// Strongly-typed view over the <c>Pipeline</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Bound with the options pattern (<c>services.AddOptions&lt;PipelineOptions&gt;().Bind(...)</c>)
/// rather than reading <c>IConfiguration["Pipeline:Model"]</c> all over the code base, so the
/// set of knobs is discoverable in one file and validated once at start-up.
/// </para>
/// <para>
/// The validation below uses the C# 14 <c>field</c> keyword: a property can now run logic in its
/// setter while still using the compiler-generated backing field. Before C# 14 you had to declare
/// <c>private int _maxInterviewQuestions;</c> by hand just to add one range check.
/// Alternative: DataAnnotations (<c>[Range(1, 20)]</c>) + <c>.ValidateDataAnnotations()</c>, which is
/// better when you want *all* errors reported together; the setter approach fails fast and also
/// protects values that are set from code, not just from configuration.
/// </para>
/// </remarks>
public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    public ModelProvider Provider { get; set; } = ModelProvider.Mock;

    /// <summary>Optional override. Defaults are chosen per provider in <c>ChatClientFactory</c>.</summary>
    public Uri? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public string Model { get; set; } = "gpt-4.1-mini";

    /// <summary>Per-agent model overrides keyed by agent name (see the <c>agent:</c> front matter in prompts/).</summary>
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int MaxInterviewQuestions
    {
        get;
        set => field = value is >= 1 and <= 20
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxInterviewQuestions), value, "Must be between 1 and 20.");
    } = 6;

    /// <summary>How many developer/tester rounds before we ship with whatever findings remain.</summary>
    public int MaxReviewRounds
    {
        get;
        set => field = value is >= 1 and <= 5
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxReviewRounds), value, "Must be between 1 and 5.");
    } = 2;

    public string OutputRoot { get; set; } = "output";

    public bool AutoAnswer { get; set; }

    /// <summary>Tracker key the work is filed under (used for branch names, Jira calls, etc.).</summary>
    public string WorkItemKey { get; set; } = "DEMO-123";

    public string ModelFor(string agentName) => Models.GetValueOrDefault(agentName, Model);
}

/// <summary>Connection settings for the "real world" tools the release agent calls.</summary>
public sealed class IntegrationOptions
{
    public const string SectionName = "Integrations";

    public AtlassianOptions Jira { get; set; } = new();
    public ConfluenceOptions Confluence { get; set; } = new();
    public GitHubOptions GitHub { get; set; } = new();
}

public class AtlassianOptions
{
    public Uri BaseUrl { get; set; } = new("https://your-org.atlassian.net");
    public string Email { get; set; } = "";
    public string? ApiToken { get; set; }

    /// <summary>
    /// When true an in-process fake HTTP handler answers instead of Atlassian. The client code is
    /// identical either way, which is the point: only the transport is swapped.
    /// </summary>
    public bool UseMock { get; set; } = true;
}

public sealed class ConfluenceOptions : AtlassianOptions
{
    public string PageId { get; set; } = "123456";
}

public sealed class GitHubOptions
{
    public string Owner { get; set; } = "your-org";
    public string Repo { get; set; } = "your-repo";
    public Uri McpEndpoint { get; set; } = new("https://api.githubcopilot.com/mcp/");
    public string? Token { get; set; }
}
