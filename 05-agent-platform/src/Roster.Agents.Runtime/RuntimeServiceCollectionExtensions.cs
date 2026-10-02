using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Roster.Agents;
using Roster.Agents.Runtime;

namespace Microsoft.Extensions.DependencyInjection;

public static class RuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent runtime: the catalog, the chat client factory with its endpoint gates, and the runner.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="contractAssemblies">Assemblies holding the <see cref="AgentContractAttribute"/> records the library's
    /// manifests name (for slice 1, the library assembly: <c>typeof(Findings).Assembly</c>).</param>
    /// <remarks>
    /// <para>The host must also register the three platform services the runner depends on, all scoped because they use
    /// the database: <see cref="IModelResolver"/> (endpoint routing and the vault), <see cref="ICapabilityBinder"/>
    /// (capability ids to tools) and <see cref="IInvocationLedger"/>.</para>
    /// <para>Lifetimes: the catalog loads the library once (singleton); the gates must be shared by every call in the
    /// process (singleton); the runner is scoped like the services it uses. Options: <see cref="AgentRunnerOptions"/>
    /// (the host's placement), <see cref="ModelClientOptions"/> (telemetry) and <see cref="AgentCatalogOptions"/>.</para>
    /// </remarks>
    public static IServiceCollection AddRosterAgentRuntime(this IServiceCollection services, params Assembly[] contractAssemblies)
    {
        services.AddOptions<AgentCatalogOptions>().Configure(o => o.ContractAssemblies.AddRange(contractAssemblies));
        services.AddOptions<AgentRunnerOptions>();
        services.AddOptions<ModelClientOptions>();

        services.TryAddSingleton<IAgentCatalog, AgentCatalog>();
        services.TryAddSingleton<EndpointGates>();
        services.TryAddSingleton<ChatClientFactory>();
        services.TryAddScoped<IAgentRunner, AgentRunner>();

        return services;
    }
}
