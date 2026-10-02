using System.Security.Claims;
using Roster.Api.Auth;
using Roster.Platform.Data;
using Roster.Platform.People;

namespace Roster.Api.Endpoints;

/// <summary>The person's own profile: the free-text title shown next to their name, and their default endpoint.</summary>
public static class ProfileEndpoints
{
    public sealed record ProfileUpdate(string? Title, Guid? DefaultEndpointId);

    public sealed record Profile(string Id, string Name, string? Email, string? Title, Guid? DefaultEndpointId);

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder me = app.MapGroup("/api/me").WithTags("Profile");

        me.MapPut("/profile", async (ProfileUpdate update, ClaimsPrincipal user, PeopleService people, CancellationToken cancellationToken) =>
        {
            AppUser person = await people.UpdateProfileAsync(user.UserId(), update.Title, update.DefaultEndpointId, cancellationToken);
            return new Profile(person.Id, person.DisplayName, person.Email, person.Title, person.DefaultEndpointId);
        });

        return app;
    }
}
