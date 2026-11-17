namespace QoffeeAutomat.Core.Platform;

/// <summary>
/// Hands out single-use login tokens for the management UI (PLAN.md S09 item 12, T711). The host of the agent implements it
/// because it owns the sessions of the management API; the Windows platform asks for a token when the interactive user, who
/// is an administrator, chooses Open UI in the tray menu, and passes it to the SessionHost over the session pipe.
/// </summary>
public interface ILoginTokenIssuer
{
    /// <summary>Creates a token. The token works once, for 30 seconds, and only from a loopback address.</summary>
    /// <returns>The token; the issuer keeps only its hash.</returns>
    string Issue();
}
