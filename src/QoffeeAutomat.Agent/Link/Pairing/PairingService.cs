using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Link.Pairing;

/// <summary>The result of a pairing request.</summary>
/// <param name="Token">The link token in plain text; null when pairing failed.</param>
/// <param name="Client">The new client record; null when pairing failed.</param>
/// <param name="Attempt">How the code was judged.</param>
public sealed record PairResult(string? Token, LinkClient? Client, PairingAttempt Attempt)
{
    /// <summary>True when the code was accepted and a token was issued.</summary>
    public bool Succeeded => Token is not null;
}

/// <summary>
/// The pairing service (PLAN.md S07, S09, D-016): shows one active code, turns a right code into a
/// link token, lists the paired clients and revokes them. Revoking a token closes the open connections
/// of that client through the <see cref="LinkConnectionRegistry"/>. The QoffeeLink server (T210) uses
/// <see cref="AuthenticateAsync"/> for the bearer check and <see cref="RegisterConnectionAsync"/> for
/// every accepted connection.
/// </summary>
public sealed class PairingService
{
    /// <summary>The client kinds that may pair (PLAN.md S07, <c>client_hello.client.kind</c>).</summary>
    public static readonly IReadOnlyList<string> ClientKinds = ["homeassistant", "hub", "tool"];

    /// <summary>The longest accepted client name.</summary>
    public const int MaxClientNameLength = 100;

    private readonly PairingCodeManager _codes;
    private readonly LinkTokenStore _tokens;
    private readonly LinkConnectionRegistry _connections;
    private readonly ITlsCertificateSource _certificate;

    /// <summary>Creates the service.</summary>
    /// <param name="codes">The active pairing code.</param>
    /// <param name="tokens">The stored tokens.</param>
    /// <param name="connections">The open connections.</param>
    /// <param name="certificate">The TLS certificate, for the fingerprint shown next to the code.</param>
    public PairingService(
        PairingCodeManager codes,
        LinkTokenStore tokens,
        LinkConnectionRegistry connections,
        ITlsCertificateSource certificate)
    {
        _codes = codes;
        _tokens = tokens;
        _connections = connections;
        _certificate = certificate;
    }

    /// <summary>The TLS fingerprint that clients pin, or null when the agent has no certificate.</summary>
    public string? Fingerprint => _certificate.Fingerprint;

    /// <summary>The registry of open connections; the QoffeeLink server registers its connections here.</summary>
    public LinkConnectionRegistry Connections => _connections;

    /// <summary>Checks a client's name and kind.</summary>
    /// <param name="client">The client data from the pairing request.</param>
    /// <returns>A message that says what is wrong, or null when the data is valid.</returns>
    public static string? ValidateClient(PairClientInfo client)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!ClientKinds.Contains(client.Kind, StringComparer.Ordinal))
        {
            return "The client kind must be homeassistant, hub or tool.";
        }

        string name = client.Name.Trim();
        if (name.Length == 0 || name.Length > MaxClientNameLength || name.Any(char.IsControl))
        {
            return "The client name must have 1 to 100 characters and no control characters.";
        }

        return null;
    }

    /// <summary>Creates a pairing code; an earlier code stops working.</summary>
    /// <returns>The code and its expiry time.</returns>
    public PairingCode StartPairing() => _codes.Start();

    /// <summary>Returns the active code.</summary>
    /// <returns>The code, or null when none is active.</returns>
    public PairingCode? ActivePairing() => _codes.GetActive();

    /// <summary>Discards the active code.</summary>
    /// <returns>True when a code was active.</returns>
    public bool CancelPairing() => _codes.Cancel();

    /// <summary>Redeems a code and issues a token.</summary>
    /// <param name="code">The code from the client.</param>
    /// <param name="client">The validated client data; see <see cref="ValidateClient"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token and the client record, or the reason the code was refused.</returns>
    public async Task<PairResult> PairAsync(string? code, PairClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        PairingAttempt attempt = _codes.TryRedeem(code);
        if (attempt != PairingAttempt.Accepted)
        {
            return new PairResult(null, null, attempt);
        }

        IssuedLinkToken issued = await _tokens.IssueAsync(client.Name.Trim(), client.Kind, cancellationToken).ConfigureAwait(false);
        return new PairResult(issued.Token, issued.Client, attempt);
    }

    /// <summary>Finds the client that owns a bearer token.</summary>
    /// <param name="token">The token from the upgrade request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The client, or null when the token is unknown or revoked (answer HTTP 401).</returns>
    public Task<LinkClient?> AuthenticateAsync(string? token, CancellationToken cancellationToken)
    {
        return _tokens.ValidateAsync(token, cancellationToken);
    }

    /// <summary>
    /// Registers an accepted connection and records the client's last-seen time. The token is checked
    /// once more after registering, so a token that was revoked between the bearer check and this call
    /// cannot keep a connection that the revocation would have closed.
    /// </summary>
    /// <param name="client">The client returned by <see cref="AuthenticateAsync"/>.</param>
    /// <param name="connection">The connection.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A registration to dispose when the connection ends, or null when the client was revoked meanwhile (close with 4401).</returns>
    public async Task<IDisposable?> RegisterConnectionAsync(LinkClient client, ILinkConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(connection);

        IDisposable registration = _connections.Register(client.Id, connection);
        IReadOnlyList<LinkClient> clients = await _tokens.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!clients.Any(known => string.Equals(known.Id, client.Id, StringComparison.Ordinal)))
        {
            registration.Dispose();
            return null;
        }

        await _tokens.TouchAsync(client.Id, cancellationToken).ConfigureAwait(false);
        return registration;
    }

    /// <summary>Lists the paired clients, oldest first.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The clients.</returns>
    public Task<IReadOnlyList<LinkClient>> ListClientsAsync(CancellationToken cancellationToken)
    {
        return _tokens.ListAsync(cancellationToken);
    }

    /// <summary>Revokes a client: its token stops working and its open connections are closed with code 4401.</summary>
    /// <param name="id">The client id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the client existed.</returns>
    public async Task<bool> RevokeAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (!await _tokens.RevokeAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _connections.CloseAllAsync(id, CloseCodes.Unauthorized, "The link token was revoked.", cancellationToken).ConfigureAwait(false);
        return true;
    }
}
