using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Link.Pairing;

/// <summary>A paired client as listed in the UI. The token hash never leaves the store.</summary>
/// <param name="Id">The client id; used to revoke the client.</param>
/// <param name="Name">The name the client gave when pairing.</param>
/// <param name="Kind">One of <c>homeassistant</c>, <c>hub</c>, <c>tool</c>.</param>
/// <param name="CreatedAt">When the client paired.</param>
/// <param name="LastSeenAt">When the client last connected; null when it never did.</param>
public sealed record LinkClient(string Id, string Name, string Kind, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

/// <summary>A freshly issued token. It exists in plain text only here and in the pairing response.</summary>
/// <param name="Token">The token, 32 random bytes as base64url without padding.</param>
/// <param name="Client">The stored client record.</param>
public sealed record IssuedLinkToken(string Token, LinkClient Client);

/// <summary>One stored token: the SHA-256 hash of the token plus the client's data.</summary>
internal sealed record LinkTokenRecord(
    string Id,
    string TokenHash,
    string Name,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt);

/// <summary>The JSON document kept under <see cref="SecretNames.LinkTokenHashes"/>.</summary>
internal sealed record LinkTokenFile(int SchemaVersion, IReadOnlyList<LinkTokenRecord>? Tokens);

/// <summary>Source-generated serialization of <see cref="LinkTokenFile"/>; no reflection is used.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(LinkTokenFile))]
internal sealed partial class LinkTokenJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The link tokens (PLAN.md S09 item 2): 32 random bytes, shown to the client once and stored as
/// SHA-256 hashes together with the client's name and kind, creation time and last-seen time. Several
/// clients can be paired; each token is revocable on its own. The list lives in the
/// <see cref="ISecretStore"/> under <see cref="SecretNames.LinkTokenHashes"/>.
/// </summary>
public sealed class LinkTokenStore : IDisposable
{
    /// <summary>The number of random bytes in a token.</summary>
    public const int TokenBytes = 32;

    private const int SchemaVersion = 1;
    private const int MaxTokenLength = 128;

    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromMinutes(1);

    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<LinkTokenRecord>? _records;

    /// <summary>Creates the store; the list is read on first use.</summary>
    /// <param name="secrets">Where the list is kept.</param>
    /// <param name="time">The clock.</param>
    public LinkTokenStore(ISecretStore secrets, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);

        _secrets = secrets;
        _time = time;
    }

    /// <summary>Creates a token for a new client and stores its hash.</summary>
    /// <param name="name">The client's name.</param>
    /// <param name="kind">The client's kind.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token in plain text and the client record.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<IssuedLinkToken> IssueAsync(string name, string kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(kind);

        byte[] raw = RandomNumberGenerator.GetBytes(TokenBytes);
        string token = Base64Url.EncodeToString(raw);
        CryptographicOperations.ZeroMemory(raw);

        var record = new LinkTokenRecord(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            HashToken(token),
            name,
            kind,
            _time.GetUtcNow(),
            null);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<LinkTokenRecord> current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var updated = new List<LinkTokenRecord>(current) { record };
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _records = updated;
        }
        finally
        {
            _gate.Release();
        }

        return new IssuedLinkToken(token, ToClient(record));
    }

    /// <summary>Finds the client that owns a token.</summary>
    /// <param name="token">The token from the <c>Authorization: Bearer</c> header.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The client, or null when the token is unknown or revoked.</returns>
    public async Task<LinkClient?> ValidateAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return null;
        }

        byte[] presented = Encoding.ASCII.GetBytes(HashToken(token));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<LinkTokenRecord> current = await LoadAsync(cancellationToken).ConfigureAwait(false);

            // Compare against every record so the time does not depend on where a match is.
            LinkTokenRecord? found = null;
            foreach (LinkTokenRecord record in current)
            {
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(record.TokenHash), presented))
                {
                    found = record;
                }
            }

            return found is null ? null : ToClient(found);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lists the paired clients, oldest first.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A snapshot of the clients.</returns>
    public async Task<IReadOnlyList<LinkClient>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<LinkTokenRecord> current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return [.. current.OrderBy(record => record.CreatedAt).Select(ToClient)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a client's token.</summary>
    /// <param name="id">The client id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the client existed.</returns>
    public async Task<bool> RevokeAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<LinkTokenRecord> current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            int index = current.FindIndex(record => string.Equals(record.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            var updated = new List<LinkTokenRecord>(current);
            updated.RemoveAt(index);
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _records = updated;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records that a client connected. The list is written at most once per minute and client.</summary>
    /// <param name="id">The client id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the time is recorded.</returns>
    public async Task TouchAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        DateTimeOffset now = _time.GetUtcNow();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<LinkTokenRecord> current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            int index = current.FindIndex(record => string.Equals(record.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            LinkTokenRecord existing = current[index];
            if (existing.LastSeenAt is { } seen && now - seen < LastSeenWriteInterval)
            {
                return;
            }

            var updated = new List<LinkTokenRecord>(current);
            updated[index] = existing with { LastSeenAt = now };
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _records = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the lock of the store.</summary>
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>The SHA-256 hash of a token as lower-case hex.</summary>
    internal static string HashToken(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static LinkClient ToClient(LinkTokenRecord record)
    {
        return new LinkClient(record.Id, record.Name, record.Kind, record.CreatedAt, record.LastSeenAt);
    }

    private async Task<List<LinkTokenRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_records is not null)
        {
            return _records;
        }

        string? json = await _secrets.GetStringAsync(SecretNames.LinkTokenHashes, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            _records = [];
            return _records;
        }

        LinkTokenFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, LinkTokenJsonContext.Default.LinkTokenFile);
        }
        catch (JsonException exception)
        {
            throw new SecretStoreException(
                SecretStoreFailure.UnreadableStore,
                "The list of link tokens cannot be read.",
                exception);
        }

        if (file is null || file.SchemaVersion != SchemaVersion)
        {
            throw new SecretStoreException(SecretStoreFailure.UnreadableStore, "The list of link tokens has an unknown format.");
        }

        var records = new List<LinkTokenRecord>();
        if (file.Tokens is not null)
        {
            records.AddRange(file.Tokens);
        }

        _records = records;
        return records;
    }

    private Task SaveAsync(List<LinkTokenRecord> records, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(new LinkTokenFile(SchemaVersion, records), LinkTokenJsonContext.Default.LinkTokenFile);
        return _secrets.SetStringAsync(SecretNames.LinkTokenHashes, json, cancellationToken);
    }
}
