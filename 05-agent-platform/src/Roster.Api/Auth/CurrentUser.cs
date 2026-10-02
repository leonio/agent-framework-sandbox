using System.Security.Claims;

namespace Roster.Api.Auth;

/// <summary>
/// The signed-in person, read from their claims. Claims keep their JWT names (<c>MapInboundClaims = false</c>), so the
/// id is <c>sub</c>, the name is <c>name</c> and roles are in <c>roles</c>.
/// </summary>
public static class CurrentUser
{
    /// <summary>The Keycloak subject: the key of the person's <c>AppUser</c> row and the owner id on everything they own.</summary>
    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? throw new InvalidOperationException("The signed-in principal has no sub claim.");

    /// <summary>The display name, falling back to the username and then the email when Keycloak has no full name.</summary>
    public static string DisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username") ?? user.FindFirstValue("email") ?? "Unknown";

    public static string? Email(this ClaimsPrincipal user) => user.FindFirstValue("email");

    /// <summary>True for Keycloak's <c>admin</c> realm role (the identity's role claim type is <c>roles</c>).</summary>
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole("admin");
}
