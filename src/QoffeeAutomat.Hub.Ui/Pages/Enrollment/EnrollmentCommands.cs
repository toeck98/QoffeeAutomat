using QoffeeAutomat.Hub.Api;

namespace QoffeeAutomat.Hub.Ui.Pages.Enrollment;

/// <summary>
/// What an administrator copies to join a PC to the hub (PLAN.md S20 "Enrollment and trust", D-026): the enrollment string itself, the command line
/// of the Windows installer and the command of the agent. The string holds a secret and is only ever shown right after the token was created.
/// </summary>
public static class EnrollmentCommands
{
    /// <summary>The start of an enrollment string.</summary>
    public const string Prefix = "qahub1.";

    /// <summary>The command line that installs the agent silently and joins the hub.</summary>
    /// <param name="enrollmentString">The enrollment string of the hub.</param>
    /// <returns>The command line; the string is quoted, though its characters never need it.</returns>
    public static string Installer(string enrollmentString)
    {
        ArgumentException.ThrowIfNullOrEmpty(enrollmentString);

        return "QoffeeAutomat-Setup.exe /VERYSILENT /HUBENROLL=\"" + enrollmentString + "\"";
    }

    /// <summary>The command that joins an agent that is installed already.</summary>
    /// <param name="enrollmentString">The enrollment string of the hub.</param>
    /// <returns>The command.</returns>
    public static string Join(string enrollmentString)
    {
        ArgumentException.ThrowIfNullOrEmpty(enrollmentString);

        return "qoffeeautomat hub join " + enrollmentString;
    }

    /// <summary>The state of an enrollment token as the table shows it.</summary>
    /// <param name="token">The token.</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>revoked</c>, <c>expired</c>, <c>used_up</c> or <c>active</c>.</returns>
    public static string StatusOf(EnrollmentTokenSummary token, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (token.RevokedAt is not null)
        {
            return "revoked";
        }

        if (token.ExpiresAt is { } expires && expires <= now)
        {
            return "expired";
        }

        return token.Uses >= token.MaxUses || !token.Active ? "used_up" : "active";
    }
}
