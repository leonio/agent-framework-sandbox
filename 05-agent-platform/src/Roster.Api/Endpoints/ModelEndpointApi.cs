using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Roster.Api.Auth;
using Roster.Platform.Data;

namespace Roster.Api.Endpoints;

/// <summary>
/// Model endpoints, as data (design doc section 8). Everyone sees the shared endpoints plus their own; the endpoint
/// picker on a new assignment offers exactly that list.
/// </summary>
/// <remarks>
/// <para><b>Who may change what.</b> A person creates, edits and deletes their own endpoints. Shared endpoints
/// (no owner) are for admins only.</para>
/// <para><b>Keys.</b> An endpoint points at a stored credential, which must belong to the person saving it. The key
/// itself never appears here; the owner sees the credential's masked hint, others see nothing.</para>
/// <para><b>Kinds.</b> <c>openai</c> (anything OpenAI-compatible) and <c>fake</c>. <c>copilot</c> arrives in slice 2.</para>
/// </remarks>
public static class ModelEndpointApi
{
    public sealed record EndpointInput(
        string Name,
        string Kind,
        string? BaseUrl,
        string DefaultModel,
        Dictionary<string, string>? TierModels,
        bool NativeStructuredOutput = true,
        bool SupportsTools = true,
        bool SupportsStreaming = true,
        bool ReasoningModel = false,
        int MaxConcurrency = 4,
        int? RequestsPerMinute = null,
        Guid? CredentialId = null,
        bool Shared = false);

    public sealed record EndpointView(
        Guid Id,
        string Name,
        string Kind,
        string? BaseUrl,
        string DefaultModel,
        Dictionary<string, string> TierModels,
        bool NativeStructuredOutput,
        bool SupportsTools,
        bool SupportsStreaming,
        bool ReasoningModel,
        int MaxConcurrency,
        int? RequestsPerMinute,
        Guid? CredentialId,
        string? CredentialHint,
        bool Shared,
        bool Mine);

    private static readonly string[] s_tiers = ["fast", "balanced", "reasoning"];

    public static IEndpointRouteBuilder MapModelEndpointApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/endpoints").WithTags("Endpoints");

        group.MapGet("/", async (ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken) =>
        {
            string me = user.UserId();
            var rows = await db.Endpoints.AsNoTracking()
                .Where(e => e.OwnerId == null || e.OwnerId == me)
                .OrderBy(e => e.OwnerId != null).ThenBy(e => e.Name)
                .Select(e => new { Endpoint = e, Hint = db.Credentials.Where(c => c.Id == e.CredentialId).Select(c => c.Hint).FirstOrDefault() })
                .ToListAsync(cancellationToken);
            return rows.Select(r => ToView(r.Endpoint, r.Hint, me));
        });

        group.MapPost("/", async (EndpointInput input, ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken) =>
        {
            if (input.Shared && !user.IsAdmin())
            {
                throw new UnauthorizedAccessException("Only an admin can create a shared endpoint.");
            }

            var endpoint = new ModelEndpoint { Id = Guid.NewGuid(), Name = "", DefaultModel = "", CreatedAt = DateTimeOffset.UtcNow };
            await ApplyAsync(endpoint, input, user, db, cancellationToken);
            endpoint.OwnerId = input.Shared ? null : user.UserId();

            db.Endpoints.Add(endpoint);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Created($"/api/endpoints/{endpoint.Id}", ToView(endpoint, hint: null, user.UserId()));
        });

        group.MapPut("/{id:guid}", async (Guid id, EndpointInput input, ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken) =>
        {
            ModelEndpoint endpoint = await FindEditableAsync(id, user, db, cancellationToken);
            await ApplyAsync(endpoint, input, user, db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return ToView(endpoint, hint: null, user.UserId());
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken) =>
        {
            ModelEndpoint endpoint = await FindEditableAsync(id, user, db, cancellationToken);
            db.Endpoints.Remove(endpoint); // Assignments and defaults that named it fall back to the routing defaults.
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    // Own endpoints are editable by their owner; shared ones by admins.
    private static async Task<ModelEndpoint> FindEditableAsync(Guid id, ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken)
    {
        ModelEndpoint endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"No endpoint {id}.");

        bool allowed = endpoint.OwnerId is null ? user.IsAdmin() : endpoint.OwnerId == user.UserId();
        return allowed ? endpoint : throw new UnauthorizedAccessException("This endpoint is not yours to change.");
    }

    // Validates the input and copies it onto the entity.
    private static async Task ApplyAsync(ModelEndpoint endpoint, EndpointInput input, ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken)
    {
        endpoint.Name = CredentialEndpoints.Required(input.Name, "name", maxLength: 80);
        endpoint.DefaultModel = CredentialEndpoints.Required(input.DefaultModel, "defaultModel", maxLength: 200);
        endpoint.Kind = input.Kind?.ToLowerInvariant() switch
        {
            "openai" => EndpointKind.OpenAI,
            "fake" => EndpointKind.Fake,
            "copilot" => throw new NotSupportedException("Copilot endpoints arrive in slice 2."),
            _ => throw new ArgumentException("Kind is openai or fake."),
        };

        if (input.BaseUrl is { Length: > 0 } url && !(Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"))
        {
            throw new ArgumentException("baseUrl must be an absolute http or https URL.");
        }

        endpoint.BaseUrl = string.IsNullOrWhiteSpace(input.BaseUrl) ? null : input.BaseUrl.Trim();

        Dictionary<string, string> tiers = (input.TierModels ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Value))
            .ToDictionary(t => t.Key.ToLowerInvariant(), t => t.Value.Trim());
        if (tiers.Keys.FirstOrDefault(k => !s_tiers.Contains(k)) is { } unknown)
        {
            throw new ArgumentException($"Unknown tier '{unknown}'. Tiers are fast, balanced and reasoning.");
        }

        endpoint.TierModelsJson = JsonSerializer.Serialize(tiers);
        endpoint.NativeStructuredOutput = input.NativeStructuredOutput;
        endpoint.SupportsTools = input.SupportsTools;
        endpoint.SupportsStreaming = input.SupportsStreaming;
        endpoint.ReasoningModel = input.ReasoningModel;
        endpoint.MaxConcurrency = input.MaxConcurrency is >= 1 and <= 64
            ? input.MaxConcurrency
            : throw new ArgumentException("maxConcurrency is between 1 and 64.");
        endpoint.RequestsPerMinute = input.RequestsPerMinute;

        if (input.CredentialId is { } credentialId
            && !await db.Credentials.AnyAsync(c => c.Id == credentialId && c.UserId == user.UserId(), cancellationToken))
        {
            throw new ArgumentException("The credential must be one of yours.");
        }

        endpoint.CredentialId = input.CredentialId;
    }

    private static EndpointView ToView(ModelEndpoint e, string? hint, string me) => new(
        e.Id,
        e.Name,
        e.Kind.ToString().ToLowerInvariant(),
        e.BaseUrl,
        e.DefaultModel,
        JsonSerializer.Deserialize<Dictionary<string, string>>(e.TierModelsJson) ?? [],
        e.NativeStructuredOutput,
        e.SupportsTools,
        e.SupportsStreaming,
        e.ReasoningModel,
        e.MaxConcurrency,
        e.RequestsPerMinute,
        e.CredentialId,
        e.OwnerId == me ? hint : null, // Only the owner sees which key it uses.
        Shared: e.OwnerId is null,
        Mine: e.OwnerId == me);
}
