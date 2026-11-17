using System.Text.RegularExpressions;

namespace QoffeeAutomat.Core.Secrets;

/// <summary>
/// The secrets of the agent, kept in <c>secrets/secrets.json</c> (PLAN.md S09): the password hash,
/// the hashes of link tokens, the TLS certificate with its private key and later script secrets.
/// Names are visible in the file, values are encrypted by an <see cref="ISecretProtector"/>.
/// </summary>
public interface ISecretStore
{
    /// <summary>Reads a secret.</summary>
    /// <param name="name">The secret's name, see <see cref="SecretNames"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A copy of the value, or null when there is none.</returns>
    /// <exception cref="SecretStoreException">The store or the key cannot be used.</exception>
    Task<byte[]?> GetBytesAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads a secret that is text (UTF-8).</summary>
    /// <param name="name">The secret's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The text, or null when there is none.</returns>
    /// <exception cref="SecretStoreException">The store or the key cannot be used.</exception>
    Task<string?> GetStringAsync(string name, CancellationToken cancellationToken);

    /// <summary>Stores a secret, replacing an existing one. The file is written atomically.</summary>
    /// <param name="name">The secret's name.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the secret is on disk.</returns>
    /// <exception cref="SecretStoreException">The store or the key cannot be used.</exception>
    Task SetBytesAsync(string name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken);

    /// <summary>Stores a secret that is text (UTF-8).</summary>
    /// <param name="name">The secret's name.</param>
    /// <param name="value">The text.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the secret is on disk.</returns>
    /// <exception cref="SecretStoreException">The store or the key cannot be used.</exception>
    Task SetStringAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>Removes a secret.</summary>
    /// <param name="name">The secret's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the secret existed.</returns>
    /// <exception cref="SecretStoreException">The store or the key cannot be used.</exception>
    Task<bool> RemoveAsync(string name, CancellationToken cancellationToken);

    /// <summary>Lists the names of all secrets, ordered.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The names.</returns>
    /// <exception cref="SecretStoreException">The store cannot be read.</exception>
    Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken);
}

/// <summary>Well-known secret names and the rules for names.</summary>
public static class SecretNames
{
    /// <summary>The salted hash of the UI password.</summary>
    public const string PasswordHash = "auth.password_hash";

    /// <summary>The SHA-256 hashes of the issued link tokens.</summary>
    public const string LinkTokenHashes = "link.token_hashes";

    /// <summary>The TLS certificate with its private key (PFX).</summary>
    public const string TlsCertificate = "tls.certificate";

    /// <summary>The SHA-256 hash (lower-case hex) of the read-only token of the Prometheus endpoint (T709); the token itself is never stored.</summary>
    public const string PrometheusTokenHash = "prometheus.token_hash";

    /// <summary>The reverse-link token that the agent presents to Home Assistant (T710, ADR 0003); unlike the hashes above it is the credential itself.</summary>
    public const string ReverseLinkToken = "reverse_link.token";

    /// <summary>The prefix of secrets that belong to scripts (after v1).</summary>
    public const string ScriptPrefix = "script.";

    private static readonly Regex NamePattern = new(
        @"^[a-z][a-z0-9_.-]{0,127}\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Tells whether a string is a valid secret name (<c>^[a-z][a-z0-9_.-]{0,127}$</c>).</summary>
    /// <param name="name">The candidate.</param>
    /// <returns>True when the name is valid.</returns>
    public static bool IsValid(string? name)
    {
        return !string.IsNullOrEmpty(name) && NamePattern.IsMatch(name);
    }
}

/// <summary>Why a secret store operation failed.</summary>
public enum SecretStoreFailure
{
    /// <summary>Unspecified.</summary>
    Unknown,

    /// <summary>The key file is readable by group or others; the store refuses to use it.</summary>
    InsecureKeyFile,

    /// <summary>The key file has the wrong size or cannot be read.</summary>
    InvalidKeyFile,

    /// <summary>The secrets file cannot be read or has an unknown format. It is left untouched.</summary>
    UnreadableStore,

    /// <summary>The secrets file was written by another protector scheme.</summary>
    ProtectorMismatch,

    /// <summary>A secret cannot be decrypted (wrong key or damaged data).</summary>
    DecryptionFailed,
}

/// <summary>The secret store or its key cannot be used. The message never contains secret values.</summary>
public sealed class SecretStoreException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public SecretStoreException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public SecretStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with a reason.</summary>
    /// <param name="reason">Why it failed.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause, if any.</param>
    public SecretStoreException(SecretStoreFailure reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>Why the operation failed.</summary>
    public SecretStoreFailure Reason { get; }
}
