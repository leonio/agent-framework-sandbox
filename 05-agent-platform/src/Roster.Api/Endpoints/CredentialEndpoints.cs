using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Roster.Api.Auth;
using Roster.Platform.Credentials;
using Roster.Platform.Data;

namespace Roster.Api.Endpoints;

/// <summary>
/// A person's stored credentials: API keys (bring your own key) and GitHub tokens (their own Copilot seat, slice 2).
/// </summary>
/// <remarks>
/// <para><b>Write-only.</b> A secret goes in once, is sealed by the vault, and never comes back out through the API:
/// listings show only the label and a masked hint. To replace a key, delete it and add the new one.</para>
/// <para><b>Own only.</b> Every query is filtered by the signed-in person; nobody, admins included, lists someone else's.</para>
/// <para><b>GitHub tokens</b> must be of a kind the Copilot SDK accepts (<c>github_pat_</c>, <c>gho_</c>, <c>ghu_</c>);
/// classic <c>ghp_</c> tokens are refused up front rather than failing later in a job.</para>
/// </remarks>
public static class CredentialEndpoints
{
    public sealed record CredentialInput(string Kind, string Label, string Secret);

    public sealed record CredentialView(Guid Id, string Kind, string Label, string Hint, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

    private static readonly string[] s_gitHubPrefixes = ["github_pat_", "gho_", "ghu_"];

    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/credentials").WithTags("Credentials");

        group.MapGet("/", async (ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken) =>
            (await db.Credentials.AsNoTracking()
                .Where(c => c.UserId == user.UserId())
                .OrderBy(c => c.CreatedAt)
                .ToListAsync(cancellationToken))
            .Select(ToView));

        group.MapPost("/", async (CredentialInput input, ClaimsPrincipal user, RosterDb db, SecretVault vault, CancellationToken cancellationToken) =>
        {
            CredentialKind kind = ParseKind(input.Kind);
            string label = Required(input.Label, "label", maxLength: 80);
            string secret = Required(input.Secret, "secret", maxLength: 4096);
            if (kind == CredentialKind.GitHubToken && !s_gitHubPrefixes.Any(p => secret.StartsWith(p, StringComparison.Ordinal)))
            {
                throw new ArgumentException("Use a fine-grained token (github_pat_) or an OAuth or GitHub App user token (gho_, ghu_). Classic ghp_ tokens do not work with Copilot.");
            }

            UserCredential credential = vault.Create(user.UserId(), kind, label, secret);
            db.Credentials.Add(credential);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Created($"/api/credentials/{credential.Id}", ToView(credential));
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken) =>
        {
            // Endpoints that used it keep working without a key (the FK sets their credential to null).
            int deleted = await db.Credentials.Where(c => c.Id == id && c.UserId == user.UserId()).ExecuteDeleteAsync(cancellationToken);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });

        return app;
    }

    private static CredentialView ToView(UserCredential c) =>
        new(c.Id, c.Kind == CredentialKind.ApiKey ? "api-key" : "github-token", c.Label, c.Hint, c.CreatedAt, c.LastUsedAt);

    private static CredentialKind ParseKind(string kind) => kind?.ToLowerInvariant() switch
    {
        "api-key" or "apikey" => CredentialKind.ApiKey,
        "github-token" or "githubtoken" => CredentialKind.GitHubToken,
        _ => throw new ArgumentException("Kind is api-key or github-token."),
    };

    internal static string Required(string? value, string name, int maxLength)
    {
        string trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            throw new ArgumentException($"{name} is required and at most {maxLength} characters.");
        }

        return trimmed;
    }
}
