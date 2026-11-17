namespace QoffeeAutomat.Core.Secrets.Named;

/// <summary>
/// The named secrets of scripts (PLAN.md S09, T706), kept in the <see cref="ISecretStore"/>. Values can be written and
/// replaced but never read back through this interface except for a run: <see cref="ResolveAsync"/> is for the command
/// engine only. The management API and the UI see names only.
/// </summary>
public interface INamedSecretStore
{
    /// <summary>Lists the names of the secrets, ordered.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The names.</returns>
    /// <exception cref="SecretStoreException">The store cannot be read.</exception>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates a secret or replaces its value.</summary>
    /// <param name="name">A valid name, see <see cref="NamedSecretNames"/>.</param>
    /// <param name="value">The value; see <see cref="NamedSecretNames.DescribeValueProblem"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the secret is new, false when an existing one was replaced.</returns>
    /// <exception cref="ArgumentException">The name or the value is not valid.</exception>
    /// <exception cref="SecretStoreException">The store or the key cannot be used.</exception>
    Task<bool> SetAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>Removes a secret.</summary>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the secret existed.</returns>
    /// <exception cref="SecretStoreException">The store or the key cannot be used.</exception>
    Task<bool> RemoveAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads the values that a run asked for.</summary>
    /// <param name="names">The names that the command lists; null or empty gives <see cref="ResolvedSecrets.None"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values, and the names that do not exist.</returns>
    /// <exception cref="SecretStoreException">The store or the key cannot be used.</exception>
    Task<ResolvedSecrets> ResolveAsync(IReadOnlyList<string>? names, CancellationToken cancellationToken);
}
