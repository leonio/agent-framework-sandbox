using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Roster.Agents;
using Roster.Platform.Credentials;
using Roster.Platform.Data;

namespace Roster.Platform.Models;

/// <summary>
/// Decides which endpoint and model serve one agent call, and opens the endpoint's credential for it. This is the
/// platform's <see cref="IModelResolver"/>; the runtime calls it at the start of every agent call.
/// </summary>
/// <remarks>
/// <para><b>Endpoint</b>, first match wins (design doc section 8):</para>
/// <list type="number">
/// <item>a step override in the assignment's advanced panel (<c>steps["review/security-reviewer"]</c>),</item>
/// <item>a phase override (<c>phases["review"]</c>),</item>
/// <item>the endpoint picked for the assignment,</item>
/// <item>the owner's default endpoint,</item>
/// <item>the shared default: the oldest shared endpoint.</item>
/// </list>
/// <para>(The design's "agent default per scenario" level is not used in slice 1: no scenario sets one.)</para>
/// <para><b>Model</b>: the first model named by a step override, a phase override or the assignment; otherwise the
/// endpoint's model for the agent's tier (<see cref="ModelEndpoint.TierModelsJson"/>); otherwise the endpoint's default
/// model. A named model is taken to be a model on the chosen endpoint; the UI only offers those.</para>
/// <para><b>Access</b>: an endpoint must be shared or belong to the assignment's owner. An override naming someone else's
/// endpoint is refused rather than silently spending their key.</para>
/// <para>Uses <see cref="IDbContextFactory{TContext}"/> with a short-lived context per call, because a scenario runs
/// several agents in parallel and a <c>DbContext</c> must never be used by two calls at once.</para>
/// </remarks>
public sealed class ModelRouter(IDbContextFactory<RosterDb> dbs, SecretVault vault, ILogger<ModelRouter> logger) : IModelResolver
{
    public async Task<ResolvedModel> ResolveAsync(AgentDefinition agent, AgentRunContext context, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);

        Assignment assignment = await db.Assignments.AsNoTracking().SingleAsync(a => a.Id == context.AssignmentId, cancellationToken);
        AssignmentOverrides overrides = AssignmentOverrides.Parse(assignment.OverridesJson);
        Choice? step = overrides.Steps?.GetValueOrDefault($"{context.PhaseKey}/{context.StepKey}");
        Choice? phase = overrides.Phases?.GetValueOrDefault(context.PhaseKey);

        Guid? endpointId = step?.EndpointId ?? phase?.EndpointId ?? assignment.EndpointId
            ?? await db.Users.Where(u => u.Id == assignment.OwnerId).Select(u => u.DefaultEndpointId).SingleOrDefaultAsync(cancellationToken);

        ModelEndpoint endpoint = endpointId is { } id
            ? await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken)
              ?? throw new InvalidOperationException($"Endpoint {id} chosen for {agent.Name} no longer exists.")
            : await db.Endpoints.AsNoTracking().Where(e => e.OwnerId == null).OrderBy(e => e.CreatedAt).FirstOrDefaultAsync(cancellationToken)
              ?? throw new InvalidOperationException("No endpoint was chosen and there is no shared endpoint to fall back to.");

        if (endpoint.OwnerId is not null && endpoint.OwnerId != assignment.OwnerId)
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.Name}' belongs to someone else and cannot be used for this assignment.");
        }

        string model = step?.Model ?? phase?.Model ?? assignment.Model ?? TierModel(endpoint, agent.Manifest.Tier) ?? endpoint.DefaultModel;
        string? apiKey = await OpenCredentialAsync(db, endpoint, cancellationToken);

        logger.LogDebug("Routed {Agent} ({Phase}/{Step}) to {Endpoint}/{Model}", agent.Name, context.PhaseKey, context.StepKey, endpoint.Name, model);

        return new ResolvedModel(
            endpoint.Id,
            endpoint.Name,
            endpoint.Kind.ToString().ToLowerInvariant(),
            endpoint.BaseUrl,
            model,
            apiKey,
            new EndpointCapabilities(endpoint.NativeStructuredOutput, endpoint.SupportsTools, endpoint.SupportsStreaming, endpoint.ReasoningModel),
            endpoint.MaxConcurrency);
    }

    // {"fast": "...", "balanced": "...", "reasoning": "..."}; keys are the tier names in lower case.
    private static string? TierModel(ModelEndpoint endpoint, ModelTier tier)
    {
        Dictionary<string, string>? map = JsonSerializer.Deserialize<Dictionary<string, string>>(endpoint.TierModelsJson);
        return map?.GetValueOrDefault(tier.ToString().ToLowerInvariant()) is { Length: > 0 } model ? model : null;
    }

    /// <summary>
    /// Opens the endpoint's key in memory. The credential must belong to the endpoint's owner; for a shared endpoint it
    /// belongs to whichever admin set it up. The key goes straight into the <see cref="ResolvedModel"/>, which the
    /// runtime never logs or serialises.
    /// </summary>
    private async Task<string?> OpenCredentialAsync(RosterDb db, ModelEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (endpoint.CredentialId is not { } credentialId)
        {
            return null;
        }

        UserCredential credential = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == credentialId, cancellationToken)
            ?? throw new InvalidOperationException($"Endpoint '{endpoint.Name}' refers to a credential that no longer exists.");

        if (endpoint.OwnerId is not null && credential.UserId != endpoint.OwnerId)
        {
            throw new InvalidOperationException($"Endpoint '{endpoint.Name}' refers to a credential its owner does not own.");
        }

        return vault.Open(credential);
    }
}

/// <summary>The advanced panel's overrides, as stored in <see cref="Assignment.OverridesJson"/>.</summary>
public sealed record AssignmentOverrides(Dictionary<string, Choice>? Phases, Dictionary<string, Choice>? Steps)
{
    public static AssignmentOverrides Parse(string json) =>
        JsonSerializer.Deserialize<AssignmentOverrides>(json, ContractJson.Options) ?? new(null, null);
}

/// <summary>An endpoint and/or model chosen for one phase or step. Either may be null.</summary>
public sealed record Choice(Guid? EndpointId, string? Model);
