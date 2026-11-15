namespace QoffeeAutomat.Contracts.Link;

/// <summary>The kind and name of the client that wants to pair.</summary>
public sealed record PairClientInfo(string Kind, string Name);

/// <summary>Body of <c>POST /link/v1/pair</c> (PLAN.md S07).</summary>
/// <param name="Code">The six-digit one-time code shown by the agent.</param>
/// <param name="Client">The pairing client.</param>
public sealed record PairRequest(string Code, PairClientInfo Client);

/// <summary>The agent identity in a pairing response.</summary>
public sealed record PairAgentInfo(string Id, string Name);

/// <summary>Response of a successful pairing request.</summary>
/// <param name="Token">The link token, 32 random bytes as base64url without padding.</param>
/// <param name="Agent">The agent that was paired.</param>
public sealed record PairResponse(string Token, PairAgentInfo Agent);
