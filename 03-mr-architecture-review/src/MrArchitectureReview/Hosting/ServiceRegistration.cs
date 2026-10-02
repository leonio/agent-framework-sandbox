using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MrArchitectureReview.Ai;
using MrArchitectureReview.Configuration;
using MrArchitectureReview.Integrations;
using MrArchitectureReview.Orchestration;
using MrArchitectureReview.Persistence;
using MrArchitectureReview.Prompts;
using MrArchitectureReview.Rag;
using MrArchitectureReview.Sources;

namespace MrArchitectureReview.Hosting;

/// <summary>
/// The composition root: every service this sample uses, in one place.
/// </summary>
/// <remarks>
/// Options (<see cref="ReviewOptions"/>) are bound by the caller, so the app binds them from configuration
/// and the tests from code. Lifetimes: everything is a singleton because nothing holds per-request state.
/// Per-run state lives in the workflow instance that <see cref="ReviewWorkflowFactory"/> builds for each run.
/// </remarks>
public static class ServiceRegistration
{
    public static IServiceCollection AddMrArchitectureReview(this IServiceCollection services)
    {
        // --- Model access ------------------------------------------------------------------------------
        services.AddSingleton<ChatClientFactory>();
        services.AddSingleton(_ => new PromptLibrary());

        // --- PR sources (tried in registration order) --------------------------------------------------
        services.AddSingleton<IPullRequestSource>(_ => new FixturePullRequestSource());
        services.AddHttpClient<GitHubPullRequestSource>(http =>
            // GitHub's API rejects requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("mr-architecture-review-sample/1.0"));
        services.AddSingleton<IPullRequestSource>(sp => sp.GetRequiredService<GitHubPullRequestSource>());
        services.AddSingleton<PullRequestSourceResolver>();

        // --- Memory, RAG, persistence ------------------------------------------------------------------
        services.AddSingleton<ReviewStore>();
        services.AddSingleton(_ => new GuidelinesSearch());

        // --- Integrations (dry-run by default) ---------------------------------------------------------
        // IHttpClientFactory + typed clients; DryRunHttpHandler intercepts at the HTTP layer.
        services.AddTransient<DryRunHttpHandler>();
        services.AddHttpClient<JiraClient>().AddHttpMessageHandler<DryRunHttpHandler>();
        services.AddHttpClient<ConfluenceClient>().AddHttpMessageHandler<DryRunHttpHandler>();
        services.AddSingleton<PublisherTools>();
        services.AddSingleton<GitHubMcpTools>();

        // --- Workflow ----------------------------------------------------------------------------------
        services.AddSingleton<ReviewerAgentFactory>();
        services.AddSingleton<ReviewWorkflowFactory>();
        services.AddSingleton<ITriageHandler>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ReviewOptions>>().Value;
            return o.AutoTriage ? new PolicyTriageHandler() : new ConsoleTriageHandler(o.Reviewer);
        });
        services.AddSingleton<ReviewRunner>();

        return services;
    }
}
