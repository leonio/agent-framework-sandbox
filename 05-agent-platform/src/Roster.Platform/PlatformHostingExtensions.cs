using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Roster.Agents;
using Roster.Agents.Library;
using Roster.Agents.Runtime;
using Roster.Platform.Credentials;
using Roster.Platform.Data;
using Roster.Platform.Ledger;
using Roster.Platform.Models;
using Roster.Platform.Queue;
using Roster.Platform.Retro;
using Roster.Platform.Scenarios;
using Roster.Platform.Sources;
using Roster.Platform.Tools;

namespace Microsoft.Extensions.Hosting;

public static class PlatformHostingExtensions
{
    /// <summary>
    /// Registers the platform and the agent runtime for a host (api, runner, migrator).
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="connectionName">The connection string name; the AppHost's Postgres database is called <c>roster</c>.</param>
    /// <remarks>
    /// <para><b>Database.</b> A pooled <see cref="IDbContextFactory{TContext}"/> for <see cref="RosterDb"/>, used by every
    /// platform service so concurrent agent calls never share a context, plus a scoped <see cref="RosterDb"/> made from it
    /// for API endpoints. Aspire's <c>EnrichNpgsqlDbContext</c> adds connection retries, a health check and telemetry.</para>
    /// <para><b>Configuration.</b> <c>ConnectionStrings:roster</c>; <c>Vault:Key</c> (required wherever credentials are
    /// opened); <c>GitHub:Token</c> (optional); <c>Models:CaptureMessageContent</c> (optional); <c>Runner:Placement</c>
    /// (<c>InProcess</c> by default, <c>Pool</c> for a sandbox runner).</para>
    /// <para><b>Lifetimes.</b> Stateless services that only use the context factory are singletons. Those that depend on
    /// the typed GitHub <see cref="HttpClient"/> (through the pull request sources) are transient, so the client's
    /// handler rotation keeps working.</para>
    /// <para>The API also calls <see cref="AddRosterEventStream"/> for live updates; runners do not need it.</para>
    /// </remarks>
    public static IHostApplicationBuilder AddRosterPlatform(this IHostApplicationBuilder builder, string connectionName = "roster")
    {
        string connectionString = builder.Configuration.GetConnectionString(connectionName)
            ?? throw new InvalidOperationException(
                $"No connection string '{connectionName}'. The AppHost provides it; outside Aspire set ConnectionStrings__{connectionName}.");

        IServiceCollection services = builder.Services;

        // Database.
        services.AddPooledDbContextFactory<RosterDb>(options => options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<RosterDb>>().CreateDbContext());
        builder.EnrichNpgsqlDbContext<RosterDb>();

        // Options.
        services.Configure<VaultOptions>(builder.Configuration.GetSection("Vault"));
        services.Configure<GitHubOptions>(builder.Configuration.GetSection("GitHub"));
        services.Configure<ModelClientOptions>(builder.Configuration.GetSection("Models"));
        services.Configure<AgentRunnerOptions>(o =>
            o.HostPlacement = Enum.TryParse(builder.Configuration["Runner:Placement"], ignoreCase: true, out Placement placement)
                ? placement
                : Placement.InProcess);

        // The agent runtime, with the library's contracts, and the platform's implementations of its interfaces.
        services.AddRosterAgentRuntime(typeof(Findings).Assembly);
        services.AddSingleton<SecretVault>();
        services.AddSingleton<IModelResolver, ModelRouter>();
        services.AddSingleton<IInvocationLedger, LedgerRecorder>();
        services.AddSingleton<ICapabilityBinder, CapabilityBinder>();

        // Queue and events.
        services.AddSingleton<IJobQueue, PostgresJobQueue>();
        services.AddSingleton<EventBus>();

        // Pull request sources.
        services.AddHttpClient<GitHubPullRequestSource>();
        services.AddTransient<IPullRequestSource>(sp => sp.GetRequiredService<GitHubPullRequestSource>());
        services.AddTransient<IPullRequestSource, FixturePullRequestSource>();
        services.AddTransient<PullRequestSources>();

        // Scenarios, triage, retro, scorecards and the runner's dispatcher.
        services.AddTransient<IScenario, PrReviewScenario>();
        services.AddTransient<ScenarioEngine>();
        services.AddTransient<FindingDecisions>();
        services.AddTransient<RetroService>();
        services.AddTransient<ScorecardService>();
        services.AddTransient<JobDispatcher>();

        return builder;
    }

    /// <summary>Live updates for API hosts: one LISTEN connection and per-assignment event streams (<see cref="EventStream"/>).</summary>
    public static IServiceCollection AddRosterEventStream(this IServiceCollection services)
    {
        services.AddSingleton<EventStream>();
        services.AddHostedService(sp => sp.GetRequiredService<EventStream>());
        return services;
    }
}
