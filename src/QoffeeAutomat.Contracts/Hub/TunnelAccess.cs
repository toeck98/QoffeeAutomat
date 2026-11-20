using System.Collections.Frozen;

namespace QoffeeAutomat.Contracts.Hub;

/// <summary>The verdict of <see cref="TunnelAccess"/> for one tunnelled call.</summary>
/// <param name="Status">0 when the call is allowed, otherwise the HTTP status of the refusal.</param>
/// <param name="Code">The machine-readable code of the refusal; null when allowed.</param>
/// <param name="Message">What is wrong, for people; null when allowed.</param>
public sealed record TunnelDecision(int Status, string? Code, string? Message)
{
    /// <summary>The call may go on.</summary>
    public static TunnelDecision Allow { get; } = new(0, null, null);

    /// <summary>True when the call may go on.</summary>
    public bool Allowed => Status == 0;

    /// <summary>Creates a refusal.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="code">The code.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The refusal.</returns>
    public static TunnelDecision Deny(int status, string code, string message) => new(status, code, message);
}

/// <summary>
/// The rules of the management tunnel (PLAN.md S20 "Tunnel authorisation on the agent"), kept in one place so that the hub can refuse early and the agent decides
/// for itself: which paths are tunnelled at all and what each hub role may do. The agent applies them to every <c>mgmt_request</c> and <c>mgmt_stream_open</c>, whatever
/// the hub checked before.
/// </summary>
public static class TunnelAccess
{
    /// <summary>Every tunnelled path starts with this prefix.</summary>
    public const string ApiPrefix = "/api/v1/";

    /// <summary>The longest path that is tunnelled.</summary>
    public const int MaxPathLength = 2048;

    /// <summary>The HTTP methods of <c>mgmt_request</c>.</summary>
    public static FrozenSet<string> Methods { get; } = new[] { "GET", "POST", "PUT", "PATCH", "DELETE" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Tells whether a string is a role of the tunnel.</summary>
    /// <param name="role">The candidate.</param>
    /// <returns>True for <see cref="TunnelRoles.Viewer"/>, <see cref="TunnelRoles.Operator"/> and <see cref="TunnelRoles.Admin"/>.</returns>
    public static bool IsRole(string? role) => role is TunnelRoles.Viewer or TunnelRoles.Operator or TunnelRoles.Admin;

    /// <summary>
    /// Checks the shape of a path: it starts with <see cref="ApiPrefix"/>, is not longer than <see cref="MaxPathLength"/>, and has no query string, fragment,
    /// backslash, percent escape, control character, empty segment or dot segment. The last rules keep a path from leaving <c>/api/v1/</c> after the agent's router
    /// has normalised it.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="error">What is wrong; null when the path is fine.</param>
    /// <returns>True when the path may be tunnelled.</returns>
    public static bool IsValidPath(string? path, out string? error)
    {
        if (path is null || !path.StartsWith(ApiPrefix, StringComparison.Ordinal))
        {
            error = "The path must start with " + ApiPrefix + ".";
            return false;
        }

        if (path.Length > MaxPathLength)
        {
            error = "The path is too long.";
            return false;
        }

        foreach (char c in path)
        {
            if (c is '?' or '#' or '\\' or '%' || char.IsControl(c))
            {
                error = "The path must not contain a query string, a fragment, an escape sequence or a control character.";
                return false;
            }
        }

        string[] segments = path[ApiPrefix.Length..].Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            bool last = i == segments.Length - 1;
            if (segment is "." or ".." || (segment.Length == 0 && !last))
            {
                error = "The path must not contain empty or dot segments.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Decides whether a role may make a call. <c>viewer</c> may only read, <c>operator</c> additionally starts runs (<c>POST /api/v1/commands/{id}/run</c> and
    /// <c>POST /api/v1/commands/test</c>), <c>admin</c> may do everything else. Two areas stay with the person at the PC for every role: the account of the agent
    /// (<c>/api/v1/auth/</c>, so the hub can neither sign in nor change the password) and the hub link itself (<c>/api/v1/hub</c>, which the hub may read but not change,
    /// so it cannot switch the local tunnel switch or make the agent leave). The path must be valid (<see cref="IsValidPath"/>).
    /// </summary>
    /// <param name="role">The role of the hub user.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The path.</param>
    /// <returns>The verdict.</returns>
    public static TunnelDecision Check(string? role, string? method, string? path)
    {
        if (!IsRole(role))
        {
            return TunnelDecision.Deny(403, HubErrorCodes.Forbidden, "The role of the user is not known to the agent.");
        }

        string verb = method?.ToUpperInvariant() ?? string.Empty;
        if (!Methods.Contains(verb) || !IsValidPath(path, out _))
        {
            return TunnelDecision.Deny(400, "bad_request", "The method or the path of the request is not valid.");
        }

        string[] segments = path![ApiPrefix.Length..].TrimEnd('/').Split('/');
        string area = segments[0];
        if (string.Equals(area, "auth", StringComparison.OrdinalIgnoreCase))
        {
            return TunnelDecision.Deny(403, HubErrorCodes.Forbidden, "The account of the agent cannot be used through the management tunnel.");
        }

        bool read = verb == "GET";
        if (string.Equals(area, "hub", StringComparison.OrdinalIgnoreCase) && !read)
        {
            return TunnelDecision.Deny(403, HubErrorCodes.Forbidden, "The hub link of the agent can only be changed on the agent itself.");
        }

        if (read || role == TunnelRoles.Admin)
        {
            return TunnelDecision.Allow;
        }

        if (role == TunnelRoles.Operator && verb == "POST" && IsRunPath(segments))
        {
            return TunnelDecision.Allow;
        }

        return TunnelDecision.Deny(
            403,
            HubErrorCodes.Forbidden,
            role == TunnelRoles.Viewer
                ? "The role viewer may only read."
                : "The role operator may read and run commands, but not change the agent.");
    }

    private static bool IsRunPath(string[] segments)
    {
        if (segments.Length == 0 || !string.Equals(segments[0], "commands", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (segments.Length == 2 && string.Equals(segments[1], "test", StringComparison.OrdinalIgnoreCase))
            || (segments.Length == 3 && segments[1].Length > 0 && string.Equals(segments[2], "run", StringComparison.OrdinalIgnoreCase));
    }
}
