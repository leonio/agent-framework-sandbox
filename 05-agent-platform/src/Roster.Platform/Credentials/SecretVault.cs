using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Roster.Platform.Data;

namespace Roster.Platform.Credentials;

public sealed class VaultOptions
{
    /// <summary>
    /// The master secret (configuration <c>Vault:Key</c>). The AppHost generates it as the secret parameter
    /// <c>vault-key</c> and keeps it in user secrets. Losing it makes every stored credential unreadable; people would
    /// have to enter their keys again.
    /// </summary>
    public string? Key { get; set; }
}

/// <summary>
/// Seals and opens the credentials people store (API keys, GitHub tokens) with AES-256-GCM.
/// </summary>
/// <remarks>
/// <para><b>Key.</b> The 256-bit encryption key is derived from <see cref="VaultOptions.Key"/> with HKDF-SHA256, so the
/// configured secret can be any long random string and the derived key is never stored anywhere.</para>
/// <para><b>Binding.</b> Each ciphertext is sealed with associated data <c>userId|credentialId|kind</c>. GCM checks it
/// on opening, so sealed bytes copied into another person's row, another credential or another kind fail to open
/// instead of quietly working for the wrong owner.</para>
/// <para><b>Layout.</b> <c>version (1 byte) | nonce (12) | ciphertext | tag (16)</c>. The version byte leaves room to
/// rotate the key or algorithm later without guessing which rows use what.</para>
/// <para><b>Use.</b> The API seals on save and from then on only shows <see cref="UserCredential.Hint"/>. Runners open a
/// credential in memory just before a model call (<see cref="ModelRouter"/>), and nothing logs or traces the result.</para>
/// </remarks>
public sealed class SecretVault
{
    private const byte Version1 = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // Fixed, public inputs to HKDF. Changing either changes the derived key, so they are part of version 1.
    private static readonly byte[] s_salt = "roster-vault"u8.ToArray();
    private static readonly byte[] s_info = "roster-vault/v1/aes-256-gcm"u8.ToArray();

    private readonly byte[] _key;

    public SecretVault(IOptions<VaultOptions> options)
    {
        string secret = options.Value.Key is { Length: >= 16 } k
            ? k
            : throw new InvalidOperationException(
                "Vault:Key is missing or shorter than 16 characters. The AppHost passes the generated vault-key parameter; " +
                "outside Aspire set Vault__Key to a long random string.");

        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), outputLength: 32, s_salt, s_info);
    }

    /// <summary>Creates a new credential row holding <paramref name="secret"/>, sealed for <paramref name="userId"/>.</summary>
    public UserCredential Create(string userId, CredentialKind kind, string label, string secret)
    {
        var credential = new UserCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = kind,
            Label = label,
            Hint = HintFor(secret),
            Sealed = [],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        credential.Sealed = Seal(credential.UserId, credential.Id, credential.Kind, secret);
        return credential;
    }

    public byte[] Seal(string userId, Guid credentialId, CredentialKind kind, string secret)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        byte[] output = new byte[1 + NonceSize + plaintext.Length + TagSize];
        Span<byte> nonce = output.AsSpan(1, NonceSize);
        Span<byte> ciphertext = output.AsSpan(1 + NonceSize, plaintext.Length);
        Span<byte> tag = output.AsSpan(1 + NonceSize + plaintext.Length, TagSize);

        output[0] = Version1;
        RandomNumberGenerator.Fill(nonce); // A fresh random nonce per seal; never reused with this key.

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(userId, credentialId, kind));
        CryptographicOperations.ZeroMemory(plaintext);
        return output;
    }

    /// <summary>Opens a stored credential. Throws if the bytes were tampered with or belong to another row.</summary>
    public string Open(UserCredential credential)
    {
        ReadOnlySpan<byte> data = credential.Sealed;
        if (data.Length < 1 + NonceSize + TagSize || data[0] != Version1)
        {
            throw new CryptographicException($"Credential {credential.Id} is not in a format this vault can open.");
        }

        int length = data.Length - 1 - NonceSize - TagSize;
        byte[] plaintext = new byte[length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(
            data.Slice(1, NonceSize),
            data.Slice(1 + NonceSize, length),
            data.Slice(1 + NonceSize + length, TagSize),
            plaintext,
            AssociatedData(credential.UserId, credential.Id, credential.Kind));

        string secret = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        return secret;
    }

    /// <summary>
    /// A masked form for the UI: the first three and last four characters (<c>sk-…3f9a</c>), or just dots for short
    /// secrets, where showing seven characters would give away too much.
    /// </summary>
    public static string HintFor(string secret) =>
        secret.Length < 16 ? "••••" : $"{secret[..3]}…{secret[^4..]}";

    private static byte[] AssociatedData(string userId, Guid credentialId, CredentialKind kind) =>
        Encoding.UTF8.GetBytes($"{userId}|{credentialId:N}|{kind}");
}
