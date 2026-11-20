namespace QoffeeAutomat.Hub.Api;

/// <summary>Body of <c>POST /api/v1/auth/setup</c>: creates the first administrator (PLAN.md S09 item 1, S20).</summary>
/// <param name="SetupToken">The content of <c>setup-token.txt</c> in the data directory, also printed to the log.</param>
/// <param name="Username">The name of the administrator.</param>
/// <param name="Password">The password of the administrator.</param>
public sealed record HubSetupRequest(string SetupToken, string Username, string Password);

/// <summary>Body of <c>POST /api/v1/auth/login</c>.</summary>
/// <param name="Username">The user name.</param>
/// <param name="Password">The password.</param>
public sealed record HubLoginRequest(string Username, string Password);

/// <summary>Who is signed in: the answer of <c>GET /api/v1/auth/me</c>, of a successful sign-in and of a successful setup.</summary>
/// <param name="Authenticated">Always true; an unauthenticated request is answered with 401.</param>
/// <param name="Name">The user name, or <c>token:&lt;name&gt;</c> for an API token.</param>
/// <param name="Role">The role: <c>admin</c>, <c>operator</c> or <c>viewer</c>.</param>
/// <param name="Method">How the request is authenticated: <c>session</c> or <c>token</c>.</param>
public sealed record HubSessionInfo(bool Authenticated, string Name, string Role, string Method);

/// <summary>A local account.</summary>
/// <param name="Id">The id.</param>
/// <param name="Username">The sign-in name.</param>
/// <param name="Role">The role.</param>
/// <param name="Disabled">True when the account cannot sign in.</param>
/// <param name="CreatedAt">When the account was created.</param>
/// <param name="UpdatedAt">When the role or the disabled flag last changed.</param>
/// <param name="PasswordChangedAt">When the password was last set.</param>
/// <param name="LastLoginAt">The last successful sign-in, if any.</param>
public sealed record UserInfo(
    string Id,
    string Username,
    string Role,
    bool Disabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset PasswordChangedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>The accounts of the hub, ordered by name.</summary>
/// <param name="Users">The accounts.</param>
public sealed record UserList(IReadOnlyList<UserInfo> Users);

/// <summary>Body of <c>POST /api/v1/users</c>.</summary>
/// <param name="Username">The sign-in name: 2 to 64 characters, letters, digits and <c>. _ - @</c>.</param>
/// <param name="Password">The initial password, 10 to 256 characters.</param>
/// <param name="Role">The role: <c>admin</c>, <c>operator</c> or <c>viewer</c>.</param>
public sealed record CreateUserRequest(string Username, string Password, string Role);

/// <summary>Body of <c>PATCH /api/v1/users/{id}</c>; members that are left out stay as they are. The sessions of the account end.</summary>
/// <param name="Role">The new role.</param>
/// <param name="Disabled">True disables the account, false enables it.</param>
public sealed record UpdateUserRequest(string? Role = null, bool? Disabled = null);

/// <summary>Body of <c>POST /api/v1/users/{id}/password</c>: an administrator sets the password of an account. The sessions of the account end.</summary>
/// <param name="NewPassword">The new password.</param>
public sealed record SetPasswordRequest(string NewPassword);

/// <summary>Body of <c>POST /api/v1/auth/password</c>: a user changes their own password. Their other sessions end.</summary>
/// <param name="CurrentPassword">The current password.</param>
/// <param name="NewPassword">The new password.</param>
public sealed record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>An API token. The secret is not part of it: it is shown once, when the token is created.</summary>
/// <param name="Id">The id.</param>
/// <param name="Name">What the token is for.</param>
/// <param name="Role">The role the token acts with.</param>
/// <param name="Prefix">The first characters of the secret.</param>
/// <param name="CreatedBy">The name of the account that created it.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="ExpiresAt">When it stops working, if it expires.</param>
/// <param name="LastUsedAt">When it was last used, if ever.</param>
/// <param name="RevokedAt">When it was revoked, if it was.</param>
/// <param name="Active">True when it still works.</param>
public sealed record TokenInfo(
    string Id,
    string Name,
    string Role,
    string Prefix,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool Active);

/// <summary>The API tokens of the hub, the newest first.</summary>
/// <param name="Tokens">The tokens, including revoked and expired ones.</param>
public sealed record TokenList(IReadOnlyList<TokenInfo> Tokens);

/// <summary>Body of <c>POST /api/v1/tokens</c>.</summary>
/// <param name="Name">What the token is for, 1 to 64 characters.</param>
/// <param name="Role">The role: <c>admin</c>, <c>operator</c> or <c>viewer</c>.</param>
/// <param name="ExpiresAt">When the token stops working; it must lie in the future. Without it the token does not expire.</param>
public sealed record CreateTokenRequest(string Name, string Role, DateTimeOffset? ExpiresAt = null);

/// <summary>A new API token: the record and the secret. The secret is shown only here and cannot be read again.</summary>
/// <param name="Token">The record.</param>
/// <param name="Secret">The secret to send as <c>Authorization: Bearer &lt;secret&gt;</c>.</param>
public sealed record CreatedToken(TokenInfo Token, string Secret);

/// <summary>An entry of the audit log.</summary>
/// <param name="Id">The sequence number.</param>
/// <param name="At">When the request finished.</param>
/// <param name="User">The user name, <c>token:&lt;name&gt;</c>, the name that was tried, or <c>anonymous</c>.</param>
/// <param name="Role">The role of the caller, if authenticated.</param>
/// <param name="AuthMethod">How the caller authenticated: <c>session</c>, <c>token</c> or <c>none</c>.</param>
/// <param name="Action">What was done, for example <c>createUser</c>.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Target">What the action was applied to, if anything.</param>
/// <param name="Agents">The ids of the agents that the action affected.</param>
/// <param name="Result">The result: <c>success</c>, <c>failure</c> or <c>denied</c>.</param>
/// <param name="Status">The HTTP status.</param>
/// <param name="RemoteAddress">The client address as the hub saw it.</param>
public sealed record AuditEntryInfo(
    long Id,
    DateTimeOffset At,
    string User,
    string? Role,
    string AuthMethod,
    string Action,
    string Method,
    string? Target,
    IReadOnlyList<string> Agents,
    string Result,
    int Status,
    string? RemoteAddress);

/// <summary>A page of the audit log, the newest entry first.</summary>
/// <param name="Entries">The entries.</param>
/// <param name="NextBefore">The value of the query parameter <c>before</c> that returns the next page; absent on the last page.</param>
public sealed record AuditPage(IReadOnlyList<AuditEntryInfo> Entries, long? NextBefore = null);

/// <summary>Who this hub is: the answer of <c>GET /api/v1/hub</c>.</summary>
/// <param name="Id">The hub id.</param>
/// <param name="Name">The name of the hub.</param>
/// <param name="Version">The version of the hub.</param>
/// <param name="TlsMode">How TLS is terminated: <c>selfsigned</c>, <c>certificate</c> or <c>none</c>.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint of the TLS certificate, absent for plain HTTP.</param>
/// <param name="SigningKey">The public key that verifies desired states: base64 of a DER SubjectPublicKeyInfo.</param>
public sealed record HubInfo(string Id, string Name, string Version, string TlsMode, string? Fingerprint, string SigningKey);
