namespace QoffeeAutomat.Hub.Fleet;

/// <summary>What a fleet token may see and control, as the management API shows it.</summary>
/// <param name="Type"><c>all</c>: every agent; <c>tags</c>: the agents that carry at least one of the tags.</param>
/// <param name="Tags">The tags of a tag scope; empty for <c>all</c>.</param>
public sealed record FleetScopeSummary(string Type, IReadOnlyList<string> Tags);

/// <summary>Body of <c>POST /api/v1/fleet-pairings</c>.</summary>
/// <param name="Name">A label for the token that the pairing produces, at most 64 characters; default the name that the client gives when it pairs.</param>
/// <param name="Scope"><c>all</c> (the default) or <c>tags</c>.</param>
/// <param name="Tags">The tags of a tag scope, at least one and at most 16, each 1 to 32 characters of a-z, 0-9, '.', '_' and '-'; must be empty or absent for the scope <c>all</c>.</param>
public sealed record CreateFleetPairingRequest(string? Name, string? Scope, IReadOnlyList<string>? Tags);

/// <summary>The answer of <c>POST /api/v1/fleet-pairings</c>: the code that is entered in Home Assistant, shown only here.</summary>
/// <param name="Code">The six-digit code.</param>
/// <param name="ExpiresAt">When the code stops working: five minutes after it was created.</param>
/// <param name="Name">The label that the token will have; empty when the name of the client is used.</param>
/// <param name="Scope">What the token that the code produces may see and control.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint of the hub certificate, which the person who pairs compares with what Home Assistant shows; absent when the hub is served without TLS.</param>
public sealed record FleetPairingInfo(
    string Code,
    DateTimeOffset ExpiresAt,
    string Name,
    FleetScopeSummary Scope,
    string? Fingerprint);

/// <summary>A fleet token as the API shows it. The secret is never part of it.</summary>
/// <param name="Id">The id of the token.</param>
/// <param name="Name">The label.</param>
/// <param name="Prefix">The first characters of the secret, to tell tokens apart.</param>
/// <param name="Scope">What the token may see and control.</param>
/// <param name="ClientKind">The kind of client that paired: <c>homeassistant</c> or <c>tool</c>.</param>
/// <param name="ClientName">The name that the client gave when it paired.</param>
/// <param name="CreatedBy">The user that created the pairing code.</param>
/// <param name="CreatedAt">When the token was issued.</param>
/// <param name="LastUsedAt">When a client last opened the fleet link with the token; absent when never.</param>
/// <param name="RevokedAt">When the token was revoked; absent while it works.</param>
/// <param name="Active">True while the token has not been revoked.</param>
/// <param name="Connections">The number of fleet connections that the token has open now.</param>
public sealed record FleetTokenSummary(
    string Id,
    string Name,
    string Prefix,
    FleetScopeSummary Scope,
    string ClientKind,
    string ClientName,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool Active,
    int Connections);

/// <summary>The answer of <c>GET /api/v1/fleet-tokens</c>.</summary>
/// <param name="Tokens">The tokens, newest first.</param>
public sealed record FleetTokenList(IReadOnlyList<FleetTokenSummary> Tokens);
