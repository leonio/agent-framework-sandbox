namespace Roster.Platform.Data;

/// <summary>
/// A person who has signed in. Keycloak owns the account (password, roles, registration); this row holds only what
/// Roster adds on top. It is created the first time someone signs in and refreshed from their claims each time after.
/// </summary>
public sealed class AppUser
{
    /// <summary>The Keycloak subject (the <c>sub</c> claim). Stable for the life of the account, so it is the key.</summary>
    public required string Id { get; set; }

    /// <summary>From the <c>name</c> claim, refreshed at each sign-in.</summary>
    public required string DisplayName { get; set; }

    public string? Email { get; set; }

    /// <summary>
    /// Free-text job title ("Security Engineer"), shown as <c>Name · Title</c>. Copied onto every finding decision and
    /// card at the moment it is made, so later feedback can be weighed by who gave it even if the title changes.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>The endpoint new assignments start with unless the person picks another (routing step 4).</summary>
    public Guid? DefaultEndpointId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

public enum CredentialKind
{
    /// <summary>An OpenAI-compatible API key (bring your own key).</summary>
    ApiKey,

    /// <summary>A GitHub token for the person's own Copilot seat (slice 2).</summary>
    GitHubToken,
}

/// <summary>
/// A secret a person stored: an API key or a GitHub token. Only the sealed bytes are kept; <see cref="SecretVault"/>
/// opens them in memory right before a model call. The UI never shows more than <see cref="Hint"/>.
/// </summary>
public sealed class UserCredential
{
    public Guid Id { get; set; }

    /// <summary>The owner. The vault binds the ciphertext to this id, so the bytes are useless in any other row.</summary>
    public required string UserId { get; set; }

    public CredentialKind Kind { get; set; }

    /// <summary>The person's own name for it ("work OpenAI key").</summary>
    public required string Label { get; set; }

    /// <summary>A masked form such as <c>sk-…3f9a</c>: enough to recognise the key, useless to steal.</summary>
    public required string Hint { get; set; }

    /// <summary>AES-256-GCM output: version byte, nonce (12 bytes), ciphertext, tag (16 bytes). See <see cref="Credentials.SecretVault"/>.</summary>
    public required byte[] Sealed { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
