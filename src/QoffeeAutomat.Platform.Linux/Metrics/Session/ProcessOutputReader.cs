using System.ComponentModel;
using System.Diagnostics;

namespace QoffeeAutomat.Platform.Linux.Metrics.Session;

/// <summary>
/// Runs a short command and returns what it writes to standard output (PLAN.md D-012, T801). The seam exists so that the logind
/// source can be tested without <c>loginctl</c>.
/// </summary>
public interface IProcessOutputReader
{
    /// <summary>Starts a program without a shell and reads its standard output.</summary>
    /// <param name="fileName">The program, found through <c>PATH</c> when it has no directory.</param>
    /// <param name="arguments">The arguments, one entry per argv element; they are never joined, split or interpreted by a shell.</param>
    /// <param name="timeout">How long the program may run; it is killed afterwards.</param>
    /// <param name="cancellationToken">Cancels the call and kills the program.</param>
    /// <returns>The output when the program ran and exited with zero; null when it is not installed, failed or timed out.</returns>
    ValueTask<string?> ReadStandardOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>The <see cref="IProcessOutputReader"/> that starts a real process. Arguments go to <see cref="ProcessStartInfo.ArgumentList"/>, one entry each.</summary>
public sealed class ProcessOutputReader : IProcessOutputReader
{
    /// <inheritdoc />
    public async ValueTask<string?> ReadStandardOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            // The program is not installed or may not be started: the caller falls back to another source.
            return null;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            process.StandardInput.Close();

            // Both streams are read, so that a program that writes a lot to either never blocks on a full pipe.
            Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? text : null;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Kill(process);
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The program ended on its own in the meantime.
        }
    }
}
