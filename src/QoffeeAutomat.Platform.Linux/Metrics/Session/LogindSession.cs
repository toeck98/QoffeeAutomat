namespace QoffeeAutomat.Platform.Linux.Metrics.Session;

/// <summary>A login session as <c>systemd-logind</c> knows it.</summary>
/// <param name="Id">The session id, for example <c>2</c> or <c>c1</c>.</param>
/// <param name="UserName">The user the session belongs to.</param>
/// <param name="Uid">The numeric user id; null when logind did not report it.</param>
/// <param name="Seat">The seat, for example <c>seat0</c>; null for a session without one (ssh, cron).</param>
/// <param name="SessionClass">The class: <c>user</c> for an ordinary login, <c>greeter</c> for a login screen, <c>background</c> and others for services.</param>
/// <param name="SessionType">The type: <c>x11</c>, <c>wayland</c>, <c>mir</c>, <c>tty</c> or <c>unspecified</c>; null when not reported.</param>
/// <param name="State">The state: <c>online</c>, <c>active</c> or <c>closing</c>.</param>
/// <param name="IsActive">The session is the foreground session of its seat.</param>
/// <param name="IsRemote">The session comes over the network (ssh).</param>
/// <param name="IsLocked">The desktop reported the session as locked (the <c>LockedHint</c>); null when logind did not say, which is not the same as unlocked.</param>
public sealed record LogindSession(
    string Id,
    string UserName,
    long? Uid,
    string? Seat,
    string SessionClass,
    string? SessionType,
    string State,
    bool IsActive,
    bool IsRemote,
    bool? IsLocked);

/// <summary>
/// Where the metrics get the login sessions from (PLAN.md S17, T801). The seam exists so that tests describe the sessions of a
/// computer without logind, and so that the production code can read them from <c>loginctl</c>, from the session files of
/// logind or, later, from D-Bus.
/// </summary>
public interface ILogindSessionSource
{
    /// <summary>Lists the login sessions.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The sessions; an empty list when nobody is logged in; null when logind cannot be asked (not installed, not running, a
    /// container without it), which a caller treats as "unknown" and not as "nobody".
    /// </returns>
    ValueTask<IReadOnlyList<LogindSession>?> ListSessionsAsync(CancellationToken cancellationToken);
}
