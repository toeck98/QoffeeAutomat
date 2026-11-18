using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Execution.Bash;

/// <summary>
/// Runs bash of a <c>runAs: user</c> command inside the session of the interactive user (PLAN.md D-009, S11, S17, T803). The runner
/// does not start the shell itself for such a run: it would run as the service account and, with the files of a service in a
/// directory that the user cannot read, could not read its own script. The session helper runs its own <see cref="BashRunner"/> and
/// keeps all files in the runtime directory of the user.
/// </summary>
public interface IUserContextBash
{
    /// <summary>Runs the invocation in the user's session and waits for the shell to end.</summary>
    /// <param name="invocation">What to run; <see cref="BashInvocation.Source"/> is always inline, the runner has read a script file already.</param>
    /// <param name="cancellationToken">Cancelling it kills the shell's process tree in the session.</param>
    /// <returns>The outcome; <see cref="QoffeeAutomat.Contracts.Link.ErrorCodes.NoSession"/> while no session is ready.</returns>
    ValueTask<ExecutionResult> RunAsync(BashInvocation invocation, CancellationToken cancellationToken);
}
