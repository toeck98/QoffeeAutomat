namespace QoffeeAutomat.Contracts.Api;

/// <summary>A pairing code as shown in the UI (<c>POST /api/v1/link/pairing</c>, PLAN.md S08).</summary>
/// <param name="Code">The six-digit one-time code.</param>
/// <param name="ExpiresAt">When the code stops working.</param>
/// <param name="Fingerprint">SHA-256 fingerprint of the TLS certificate, hex pairs separated by colons.</param>
public sealed record PairingInfo(string Code, DateTimeOffset ExpiresAt, string Fingerprint);

/// <summary>A paired QoffeeLink client (<c>GET /api/v1/link/clients</c>). The token itself is never shown again.</summary>
/// <param name="Id">The client id used to revoke it.</param>
/// <param name="Name">The name the client gave when it paired.</param>
/// <param name="Kind">One of <c>homeassistant</c>, <c>hub</c>, <c>tool</c>.</param>
/// <param name="CreatedAt">When the client paired.</param>
/// <param name="LastSeenAt">When the client last connected; absent when it never did.</param>
/// <param name="Connections">The number of open connections of this client.</param>
public sealed record LinkClientInfo(
    string Id,
    string Name,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    int Connections);

/// <summary>Response of <c>GET /api/v1/link/clients</c>.</summary>
/// <param name="Clients">The paired clients, oldest first.</param>
public sealed record LinkClientList(IReadOnlyList<LinkClientInfo> Clients);
