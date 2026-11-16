using QoffeeAutomat.Agent.Hosting;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>
/// The CLI verb <c>reset-password</c> (PLAN.md T212, S09 item 1): writes a new setup token to
/// <c>secrets/setup-token.txt</c>. While that file exists the UI offers the first-run setup page, where the
/// token and a new password can be entered; setting the password deletes the file and ends all sessions.
/// The old password keeps working until then. The verb needs a local administrator (Windows) or root
/// (Linux): only they can write to the secrets directory, and the token must stay readable by them alone.
/// It does not print the token; the administrator reads the file.
/// </summary>
public static class ResetPasswordCommand
{
    /// <summary>Tells whether the process runs with administrator rights (Windows) or as root (Unix).</summary>
    /// <returns>True when the process is privileged.</returns>
    public static bool IsPrivileged() => Environment.IsPrivilegedProcess;

    /// <summary>Runs the verb.</summary>
    /// <param name="options">The parsed command line; <see cref="AgentOptions.DataDir"/> selects the data directory.</param>
    /// <param name="output">Receives the confirmation.</param>
    /// <param name="error">Receives error messages.</param>
    /// <param name="isPrivileged">Tells whether the caller is an administrator or root; <see cref="IsPrivileged"/> in production.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// <see cref="ExitCodes.Success"/>; <see cref="ExitCodes.NoPermission"/> when the caller is not
    /// privileged or may not write to the secrets directory; <see cref="ExitCodes.Failure"/> for any other problem.
    /// </returns>
    public static async Task<int> RunAsync(
        AgentOptions options,
        TextWriter output,
        TextWriter error,
        Func<bool> isPrivileged,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(isPrivileged);

        if (!isPrivileged())
        {
            await error.WriteLineAsync("reset-password needs a local administrator (Windows) or root (Linux). Run it from an elevated prompt or with sudo.").ConfigureAwait(false);
            return ExitCodes.NoPermission;
        }

        var tokenService = new SetupTokenService(AppPaths.ForCurrentProcess(options.DataDir));
        try
        {
            await tokenService.CreateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            await error.WriteLineAsync("The setup token file could not be written: " + exception.Message).ConfigureAwait(false);
            return ExitCodes.NoPermission;
        }
        catch (IOException exception)
        {
            await error.WriteLineAsync("The setup token file could not be written: " + exception.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        await output.WriteLineAsync("A new setup token was written to " + tokenService.FilePath + ".").ConfigureAwait(false);
        await output.WriteLineAsync("Open the UI and enter the token on the setup page to set a new password. The old password keeps working until then.").ConfigureAwait(false);
        return ExitCodes.Success;
    }
}
