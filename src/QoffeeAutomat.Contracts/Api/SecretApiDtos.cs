namespace QoffeeAutomat.Contracts.Api;

/// <summary>
/// A named secret as the management API shows it (<c>GET /api/v1/secrets</c>, PLAN.md S08, S09, T706). Values are write-only:
/// no endpoint returns one.
/// </summary>
/// <param name="Name">The name, <c>^[a-z][a-z0-9_]{0,62}$</c>; a run receives the value in <c>QA_SECRET_&lt;NAME&gt;</c>.</param>
/// <param name="UsedBy">The ids of the commands that list the secret in <c>secrets</c>; null when none does.</param>
public sealed record SecretSummary(string Name, IReadOnlyList<string>? UsedBy = null);

/// <summary>Response of <c>GET /api/v1/secrets</c>.</summary>
/// <param name="Secrets">The secrets, ordered by name.</param>
public sealed record SecretList(IReadOnlyList<SecretSummary> Secrets);

/// <summary>Body of <c>PUT /api/v1/secrets/{name}</c>.</summary>
/// <param name="Value">The new value (4 to 4096 characters). It is stored encrypted and never shown again.</param>
public sealed record SecretWriteRequest(string Value);
