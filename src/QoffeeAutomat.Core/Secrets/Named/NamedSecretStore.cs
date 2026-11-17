namespace QoffeeAutomat.Core.Secrets.Named;

/// <summary>The default <see cref="INamedSecretStore"/>: named secrets under <c>script.&lt;name&gt;</c> in an <see cref="ISecretStore"/>.</summary>
public sealed class NamedSecretStore : INamedSecretStore
{
    private readonly ISecretStore _store;

    /// <summary>Creates the store.</summary>
    /// <param name="store">The secrets file that holds the values.</param>
    public NamedSecretStore(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> all = await _store.ListNamesAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. all
                .Where(name => name.StartsWith(SecretNames.ScriptPrefix, StringComparison.Ordinal))
                .Select(name => name[SecretNames.ScriptPrefix.Length..])
                .Where(NamedSecretNames.IsValid)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        RequireValidName(name);
        if (NamedSecretNames.DescribeValueProblem(value) is { } problem)
        {
            throw new ArgumentException(problem, nameof(value));
        }

        string storeName = NamedSecretNames.StoreName(name);
        bool existed = await _store.GetBytesAsync(storeName, cancellationToken).ConfigureAwait(false) is not null;
        await _store.SetStringAsync(storeName, value, cancellationToken).ConfigureAwait(false);
        return !existed;
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        RequireValidName(name);

        return _store.RemoveAsync(NamedSecretNames.StoreName(name), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ResolvedSecrets> ResolveAsync(IReadOnlyList<string>? names, CancellationToken cancellationToken)
    {
        if (names is null || names.Count == 0)
        {
            return ResolvedSecrets.None;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (string name in names.Distinct(StringComparer.Ordinal))
        {
            string? value = NamedSecretNames.IsValid(name)
                ? await _store.GetStringAsync(NamedSecretNames.StoreName(name), cancellationToken).ConfigureAwait(false)
                : null;
            if (value is null)
            {
                missing.Add(name);
            }
            else
            {
                values[name] = value;
            }
        }

        return new ResolvedSecrets(values, missing);
    }

    private static void RequireValidName(string name)
    {
        if (!NamedSecretNames.IsValid(name))
        {
            throw new ArgumentException("A secret name must match ^[a-z][a-z0-9_]{0,62}$.", nameof(name));
        }
    }
}
