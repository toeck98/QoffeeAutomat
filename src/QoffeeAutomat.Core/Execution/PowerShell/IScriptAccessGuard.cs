namespace QoffeeAutomat.Core.Execution.PowerShell;

/// <summary>The decision of an <see cref="IScriptAccessGuard"/>.</summary>
/// <param name="IsAllowed">True when the script may run with the requested identity.</param>
/// <param name="Reason">Why the script is refused; the message goes to the caller of the run and never contains secrets.</param>
public sealed record ScriptAccessDecision(bool IsAllowed, string? Reason = null)
{
    /// <summary>The script may run.</summary>
    public static ScriptAccessDecision Allowed { get; } = new(true);

    /// <summary>Refuses the script.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The decision.</returns>
    public static ScriptAccessDecision Refused(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether a script file may run with elevated rights (PLAN.md S09 item 5): a <c>runAs: system</c>
/// script whose file or directory standard users can change would let them run code as LocalSystem. The
/// Windows platform implements the check with file system ACLs; platforms without a check allow everything.
/// </summary>
public interface IScriptAccessGuard
{
    /// <summary>Checks a script file before it is started.</summary>
    /// <param name="scriptPath">The full path of the script file.</param>
    /// <param name="runAs">One of <see cref="QoffeeAutomat.Contracts.Config.RunAsValues"/>.</param>
    /// <returns>The decision. Implementations log a security warning when they refuse.</returns>
    ScriptAccessDecision Check(string scriptPath, string runAs);
}
