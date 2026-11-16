using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Execution.PowerShell;

/// <summary>
/// Runs PowerShell of a <c>runAs: user</c> command inside the session of the interactive user (PLAN.md D-009, S11).
/// The runner does not prepare the wrapper and the parameter file itself for such a run: the files of a service
/// would sit in a directory that the user cannot read, and the parameter file may hold secret values. The session
/// runs its own <see cref="PowerShellRunner"/> over the same wrapper code and keeps all files in the user's own
/// temporary directory.
/// </summary>
public interface IUserContextPowerShell
{
    /// <summary>Runs the invocation in the user's session and waits for the engine to end.</summary>
    /// <param name="invocation">What to run; <see cref="PowerShellInvocation.RunAs"/> is <c>user</c>.</param>
    /// <param name="cancellationToken">Cancelling it kills the engine's process tree in the session.</param>
    /// <returns>
    /// The outcome; <see cref="QoffeeAutomat.Contracts.Link.ErrorCodes.NoSession"/> while no session is ready.
    /// </returns>
    ValueTask<ExecutionResult> RunAsync(PowerShellInvocation invocation, CancellationToken cancellationToken);
}
