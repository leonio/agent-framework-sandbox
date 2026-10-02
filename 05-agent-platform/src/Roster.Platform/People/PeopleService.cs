using Microsoft.EntityFrameworkCore;
using Npgsql;
using Roster.Platform.Data;

namespace Roster.Platform.People;

/// <summary>
/// The platform's side of people. Keycloak owns accounts; this keeps the matching <see cref="AppUser"/> rows, which
/// assignments, credentials and endpoints hang off, and the profile fields Roster adds (title, default endpoint).
/// </summary>
public sealed class PeopleService(IDbContextFactory<RosterDb> dbs)
{
    private const int MaxTitleLength = 80;

    /// <summary>
    /// Called whenever someone signs in (and by <c>/api/auth/me</c>): creates their row the first time, otherwise
    /// refreshes their name and email from the identity provider, which is where people change them.
    /// </summary>
    public async Task<AppUser> SignedInAsync(string subject, string displayName, string? email, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AppUser? person = await db.Users.SingleOrDefaultAsync(u => u.Id == subject, cancellationToken);

        if (person is null)
        {
            person = new AppUser { Id = subject, DisplayName = displayName, Email = email, CreatedAt = now, LastSeenAt = now };
            db.Users.Add(person);
        }
        else
        {
            person.DisplayName = displayName;
            person.Email = email;
            person.LastSeenAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two first requests at once (two tabs): the other one created the row. Read it.
            await using RosterDb fresh = await dbs.CreateDbContextAsync(cancellationToken);
            return await fresh.Users.AsNoTracking().SingleAsync(u => u.Id == subject, cancellationToken);
        }

        return person;
    }

    public async Task<AppUser?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    /// <summary>
    /// Sets the person's title and default endpoint. The title is free text ("Security Engineer"), at most 80
    /// characters; the default endpoint must be shared or theirs.
    /// </summary>
    public async Task<AppUser> UpdateProfileAsync(string id, string? title, Guid? defaultEndpointId, CancellationToken cancellationToken = default)
    {
        string? cleanTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (cleanTitle is { Length: > MaxTitleLength })
        {
            throw new ArgumentException($"A title is at most {MaxTitleLength} characters.", nameof(title));
        }

        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        AppUser person = await db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Sign in again: there is no profile for this account yet.");

        if (defaultEndpointId is { } endpointId
            && !await db.Endpoints.AnyAsync(e => e.Id == endpointId && (e.OwnerId == null || e.OwnerId == id), cancellationToken))
        {
            throw new ArgumentException("The default endpoint must be a shared endpoint or one of yours.", nameof(defaultEndpointId));
        }

        person.Title = cleanTitle;
        person.DefaultEndpointId = defaultEndpointId;
        await db.SaveChangesAsync(cancellationToken);
        return person;
    }
}
