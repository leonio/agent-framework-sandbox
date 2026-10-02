namespace MrArchitectureReview.Configuration;

// -------------------------------------------------------------------------------------------------
// Strongly-typed configuration, bound from appsettings.json + user-secrets + environment variables +
// command line (in that precedence order, last wins) by the generic host in Program.cs.
//
// WHY the options pattern instead of Environment.GetEnvironmentVariable("...") (which the older
// samples in the Agent-Framework-Samples repo use)?
//  * One place documents every setting, with defaults.
//  * Secrets can come from user-secrets locally and Key Vault / env vars in CI without code changes.
//  * Classes are injectable and trivially constructed in tests.
// Docs: https://learn.microsoft.com/dotnet/core/extensions/options
// -------------------------------------------------------------------------------------------------

public enum AiProvider
{
    /// <summary>Scripted, offline responses from MockResponses/*.json. No keys, no network, deterministic.</summary>
    Mock,
    /// <summary>api.openai.com (or any OpenAI-compatible endpoint via <see cref="AiOptions.Endpoint"/>).</summary>
    OpenAI,
    /// <summary>Azure OpenAI / Azure AI Foundry through the OpenAI-compatible <c>/openai/v1/</c> endpoint.</summary>
    AzureOpenAI,
    /// <summary>GitHub Models (https://models.github.ai/inference) using a GitHub token.</summary>
    GitHubModels,
}

public sealed class AiOptions
{
    public AiProvider Provider { get; set; } = AiProvider.Mock;
    public string Model { get; set; } = "gpt-4.1-mini";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
}

public sealed class StorageOptions
{
    /// <summary>
    /// Where review records (AI findings + human decisions) are written.
    /// </summary>
    /// <remarks>
    /// C# 14 <b><c>field</c> keyword</b>: the setter can validate/normalise using the compiler-generated
    /// backing field without us declaring a private <c>_dataDirectory</c>. Before C# 14 you had to drop
    /// back to a full property with an explicit field the moment you needed any logic.
    /// Docs: https://learn.microsoft.com/dotnet/csharp/language-reference/keywords/field
    /// </remarks>
    public string DataDirectory
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? ".review-data" : value.Trim();
    } = ".review-data";

    /// <summary>Where PRs are cloned. Disposable; safe to delete at any time.</summary>
    public string WorkDirectory
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? ".work" : value.Trim();
    } = ".work";
}

public sealed class ReviewSettings
{
    /// <summary>
    /// Diffs larger than this are truncated before being sent to the reviewers.
    /// </summary>
    /// <remarks>
    /// "Surface level" review is a deliberate scope choice. For large PRs the better design is
    /// map-reduce: review per file (or per directory) in parallel, then a reducer agent merges results.
    /// The workflow engine supports that shape directly (dynamic fan-out with
    /// <c>AddFanOutEdge(source, targets, partitioner)</c>), we just keep this sample small.
    /// </remarks>
    public int MaxDiffCharacters { get; set; } = 60_000;

    /// <summary>How many past human decisions to show each reviewer as calibration examples.</summary>
    public int HistoryExamples { get; set; } = 6;

    /// <summary>Findings below this confidence are dropped before a human ever sees them.</summary>
    public double MinimumConfidence { get; set; } = 0.3;
}

public sealed class GitHubOptions
{
    /// <summary>Optional. Raises API rate limits and allows private repositories.</summary>
    public string? Token { get; set; }
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";
}

public sealed class IntegrationOptions
{
    /// <summary>
    /// When true (the default) no request leaves the machine: Jira/Confluence/GitHub calls are
    /// logged and answered with a canned success. Flip to false only once real URLs and tokens are set.
    /// </summary>
    public bool DryRun { get; set; } = true;

    public string JiraBaseUrl { get; set; } = "https://your-company.atlassian.net/";
    public string JiraProjectKey { get; set; } = "ARCH";
    public string ConfluenceBaseUrl { get; set; } = "https://your-company.atlassian.net/wiki/";
    public string ConfluenceSpaceKey { get; set; } = "ENG";
    public string ConfluenceDecisionLogPageId { get; set; } = "123456";

    /// <summary>Atlassian Cloud uses Basic auth with email + API token.</summary>
    public string? AtlassianEmail { get; set; }
    public string? AtlassianApiToken { get; set; }
}

/// <summary>Root options object, bound from the <c>"Review"</c> configuration section.</summary>
public sealed class ReviewOptions
{
    public const string SectionName = "Review";

    public AiOptions Ai { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public ReviewSettings Settings { get; set; } = new();
    public GitHubOptions GitHub { get; set; } = new();
    public IntegrationOptions Integrations { get; set; } = new();

    /// <summary>Who is triaging. Recorded with every decision.</summary>
    public string Reviewer { get; set; } = Environment.UserName;

    /// <summary>
    /// Non-interactive mode: decisions are made by a simple policy instead of console prompts.
    /// Used by the tests and handy for a CI dry run.
    /// </summary>
    public bool AutoTriage { get; set; }
}
