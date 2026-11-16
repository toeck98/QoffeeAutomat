using System.Globalization;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>
/// The management password (PLAN.md S09 item 1): a salted PBKDF2-SHA256 hash kept in the secret store
/// under <see cref="SecretNames.PasswordHash"/>. The password itself is never stored or logged.
/// </summary>
public sealed class PasswordService
{
    private readonly ISecretStore _secrets;
    private readonly AuthOptions _options;

    /// <summary>Creates the service.</summary>
    /// <param name="secrets">Where the hash is kept.</param>
    /// <param name="options">The password length limits.</param>
    public PasswordService(ISecretStore secrets, AuthOptions options)
    {
        _secrets = secrets;
        _options = options;
    }

    /// <summary>Tells whether a password has been set.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when a hash is stored.</returns>
    public async Task<bool> HasPasswordAsync(CancellationToken cancellationToken)
    {
        return await _secrets.GetStringAsync(SecretNames.PasswordHash, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Checks a candidate against the password policy.</summary>
    /// <param name="password">The candidate.</param>
    /// <returns>A message that says what is wrong, or null when the password is acceptable.</returns>
    public string? ValidatePolicy(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length < _options.MinPasswordLength)
        {
            return "The password must have at least " + _options.MinPasswordLength.ToString(CultureInfo.InvariantCulture) + " characters.";
        }

        if (password.Length > _options.MaxPasswordLength)
        {
            return "The password must have at most " + _options.MaxPasswordLength.ToString(CultureInfo.InvariantCulture) + " characters.";
        }

        return null;
    }

    /// <summary>Hashes and stores a password, replacing the old one.</summary>
    /// <param name="password">The password; it must satisfy <see cref="ValidatePolicy"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the hash is stored.</returns>
    public async Task SetPasswordAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);

        string hash = PasswordHasher.Hash(password);
        await _secrets.SetStringAsync(SecretNames.PasswordHash, hash, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks a password against the stored hash.</summary>
    /// <param name="password">The password to check.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when a password is set and matches; false for a wrong password or when none is set.</returns>
    public async Task<bool> VerifyAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length > _options.MaxPasswordLength)
        {
            return false;
        }

        string? stored = await _secrets.GetStringAsync(SecretNames.PasswordHash, cancellationToken).ConfigureAwait(false);
        return stored is not null && PasswordHasher.Verify(password, stored);
    }
}
