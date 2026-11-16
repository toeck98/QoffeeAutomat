namespace QoffeeAutomat.Contracts.Api;

/// <summary>Response of <c>GET /api/v1/auth/setup</c>: first-run setup is possible.</summary>
/// <param name="Available">Always true; when setup is not possible the endpoint answers 404 instead.</param>
public sealed record SetupState(bool Available);

/// <summary>Body of <c>POST /api/v1/auth/setup</c> (PLAN.md S08, S09 item 1).</summary>
/// <param name="SetupToken">The content of <c>secrets/setup-token.txt</c>.</param>
/// <param name="Password">The password to set.</param>
public sealed record SetupRequest(string SetupToken, string Password);

/// <summary>Body of <c>POST /api/v1/auth/login</c>.</summary>
/// <param name="Password">The password.</param>
public sealed record LoginRequest(string Password);

/// <summary>Body of <c>POST /api/v1/auth/password</c>.</summary>
/// <param name="CurrentPassword">The current password.</param>
/// <param name="NewPassword">The new password.</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Response of <c>GET /api/v1/auth/me</c>, of a successful login and of a successful setup.</summary>
/// <param name="Authenticated">True when the request carries a valid session.</param>
/// <param name="Name">The name of the signed-in account.</param>
public sealed record AuthMeResponse(bool Authenticated, string Name);
