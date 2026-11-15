using System.Collections.Frozen;

namespace QoffeeAutomat.Contracts.Link;

/// <summary>Error codes of QoffeeLink v1 (PLAN.md S07). Receivers must tolerate unknown codes.</summary>
public static class ErrorCodes
{
    /// <summary>The link token is unknown or revoked.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>No common protocol version.</summary>
    public const string UnsupportedVersion = "unsupported_version";

    /// <summary>The frame or its payload is malformed.</summary>
    public const string BadRequest = "bad_request";

    /// <summary>The command id does not exist.</summary>
    public const string UnknownCommand = "unknown_command";

    /// <summary>The command exists but is disabled.</summary>
    public const string CommandDisabled = "command_disabled";

    /// <summary>A variable is missing, unknown or invalid.</summary>
    public const string InvalidVars = "invalid_vars";

    /// <summary>The command is already running and its concurrency mode forbids another run.</summary>
    public const string Busy = "busy";

    /// <summary>The command is within its cooldown period.</summary>
    public const string Cooldown = "cooldown";

    /// <summary>The command needs a logged-in user and nobody is logged in.</summary>
    public const string NoSession = "no_session";

    /// <summary>The command needs elevation and the user is not an administrator.</summary>
    public const string ElevationUnavailable = "elevation_unavailable";

    /// <summary>The run was cancelled.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The run exceeded its timeout.</summary>
    public const string Timeout = "timeout";

    /// <summary>The agent is paused (maintenance mode).</summary>
    public const string Paused = "paused";

    /// <summary>Unexpected error in the agent.</summary>
    public const string Internal = "internal";

    /// <summary>All error codes defined by protocol version 1.</summary>
    public static FrozenSet<string> All { get; } = new[]
    {
        Unauthorized,
        UnsupportedVersion,
        BadRequest,
        UnknownCommand,
        CommandDisabled,
        InvalidVars,
        Busy,
        Cooldown,
        NoSession,
        ElevationUnavailable,
        Cancelled,
        Timeout,
        Paused,
        Internal,
    }.ToFrozenSet(StringComparer.Ordinal);
}
