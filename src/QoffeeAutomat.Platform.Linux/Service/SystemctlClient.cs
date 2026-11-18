using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>How a program that was run to the end finished.</summary>
/// <param name="ExitCode">The exit code; zero means success.</param>
/// <param name="StandardOutput">What it wrote to standard output.</param>
/// <param name="StandardError">What it wrote to standard error.</param>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>True when the program exited with zero.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs a program and reports how it ended; the seam that lets tests replace <c>systemctl</c> and the other programs the verbs use (PLAN.md D-012).</summary>
public interface ICommandRunner
{
    /// <summary>Starts a program without a shell and waits for it.</summary>
    /// <param name="fileName">The program, found through <c>PATH</c> when it has no directory.</param>
    /// <param name="arguments">The arguments, one entry per argv element; never joined, split or interpreted by a shell.</param>
    /// <param name="timeout">How long the program may run; it is killed afterwards.</param>
    /// <param name="cancellationToken">Cancels the call and kills the program.</param>
    /// <returns>The result; null when the program is not installed, cannot be started or did not finish in time.</returns>
    ValueTask<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>The <see cref="ICommandRunner"/> that starts a real process. Arguments go to <see cref="ProcessStartInfo.ArgumentList"/>, one entry each.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    /// <inheritdoc />
    public async ValueTask<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
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
            return null;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return new CommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
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

/// <summary>A <c>systemctl</c> call failed; the message says what systemd answered.</summary>
public sealed class SystemctlException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public SystemctlException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public SystemctlException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public SystemctlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What <c>systemctl show</c> reports about a unit.</summary>
/// <param name="Name">The unit name, for example <c>qoffeeautomat.service</c>.</param>
/// <param name="LoadState"><c>loaded</c> for a unit that systemd knows, <c>not-found</c> for one that does not exist.</param>
/// <param name="ActiveState"><c>active</c>, <c>inactive</c>, <c>activating</c>, <c>deactivating</c> or <c>failed</c>.</param>
/// <param name="SubState">The detail, for example <c>running</c> or <c>dead</c>.</param>
/// <param name="UnitFileState"><c>enabled</c>, <c>disabled</c> and so on; empty when the unit has no file.</param>
/// <param name="MainPid">The process id of the main process; 0 when none runs.</param>
/// <param name="FragmentPath">The unit file; empty when there is none.</param>
public sealed record UnitStatus(
    string Name,
    string LoadState,
    string ActiveState,
    string SubState,
    string UnitFileState,
    int MainPid,
    string FragmentPath)
{
    /// <summary>True when systemd knows the unit.</summary>
    public bool IsInstalled => !string.Equals(LoadState, "not-found", StringComparison.Ordinal);

    /// <summary>True when the unit runs.</summary>
    public bool IsActive => string.Equals(ActiveState, "active", StringComparison.Ordinal);
}

/// <summary>The part of <c>systemctl</c> that the verbs use.</summary>
public interface ISystemctl
{
    /// <summary>Reads the unit files again (<c>daemon-reload</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when systemd has reloaded.</returns>
    /// <exception cref="SystemctlException">systemctl is missing or refused.</exception>
    Task DaemonReloadAsync(CancellationToken cancellationToken);

    /// <summary>Makes the unit start at boot.</summary>
    /// <param name="unit">The unit name with suffix.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the unit is enabled.</returns>
    /// <exception cref="SystemctlException">systemctl is missing or refused.</exception>
    Task EnableAsync(string unit, CancellationToken cancellationToken);

    /// <summary>Stops the unit from starting at boot.</summary>
    /// <param name="unit">The unit name with suffix.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the unit is disabled.</returns>
    /// <exception cref="SystemctlException">systemctl is missing or refused.</exception>
    Task DisableAsync(string unit, CancellationToken cancellationToken);

    /// <summary>Starts the unit and waits until it is up: a <c>Type=notify</c> unit counts as up when it sent <c>READY=1</c>.</summary>
    /// <param name="unit">The unit name with suffix.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the unit runs.</returns>
    /// <exception cref="SystemctlException">systemctl is missing, or the unit did not start.</exception>
    Task StartAsync(string unit, CancellationToken cancellationToken);

    /// <summary>Stops the unit and waits until it is down.</summary>
    /// <param name="unit">The unit name with suffix.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the unit is stopped.</returns>
    /// <exception cref="SystemctlException">systemctl is missing, or the unit did not stop.</exception>
    Task StopAsync(string unit, CancellationToken cancellationToken);

    /// <summary>Reads the state of a unit.</summary>
    /// <param name="unit">The unit name with suffix.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state; a unit that does not exist has <see cref="UnitStatus.IsInstalled"/> false.</returns>
    /// <exception cref="SystemctlException">systemctl is missing or the answer cannot be read.</exception>
    Task<UnitStatus> QueryAsync(string unit, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ISystemctl"/> through the program <c>systemctl</c> (PLAN.md S17, T802). Unit names are checked against the pattern of
/// systemd before they reach a command line, and every argument is a separate argv entry, so no name can add an option.
/// </summary>
public sealed partial class SystemctlClient : ISystemctl
{
    private static readonly string[] ShowProperties =
    [
        "LoadState", "ActiveState", "SubState", "UnitFileState", "MainPID", "FragmentPath",
    ];

    private static readonly Regex UnitNamePattern = UnitNameRegex();

    private readonly ICommandRunner _runner;
    private readonly string _fileName;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the client.</summary>
    /// <param name="runner">Runs the program.</param>
    /// <param name="fileName">The program; <c>systemctl</c> is looked up in <c>PATH</c>.</param>
    /// <param name="timeout">How long one call may take; null uses <see cref="LinuxServiceDefaults.ControlTimeout"/>.</param>
    public SystemctlClient(ICommandRunner runner, string fileName = "systemctl", TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        _runner = runner;
        _fileName = fileName;
        _timeout = timeout ?? LinuxServiceDefaults.ControlTimeout;
    }

    /// <summary>Tells whether a string is a plain service unit name such as <c>qoffeeautomat.service</c>.</summary>
    /// <param name="unit">The candidate.</param>
    /// <returns>True when it can safely be given to <c>systemctl</c>.</returns>
    public static bool IsValidUnitName(string unit) => UnitNamePattern.IsMatch(unit);

    /// <inheritdoc />
    public Task DaemonReloadAsync(CancellationToken cancellationToken) => RunAsync(["daemon-reload"], cancellationToken);

    /// <inheritdoc />
    public Task EnableAsync(string unit, CancellationToken cancellationToken) => RunAsync(["enable", Checked(unit)], cancellationToken);

    /// <inheritdoc />
    public Task DisableAsync(string unit, CancellationToken cancellationToken) => RunAsync(["disable", Checked(unit)], cancellationToken);

    /// <inheritdoc />
    public Task StartAsync(string unit, CancellationToken cancellationToken) => RunAsync(["start", Checked(unit)], cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(string unit, CancellationToken cancellationToken) => RunAsync(["stop", Checked(unit)], cancellationToken);

    /// <inheritdoc />
    public async Task<UnitStatus> QueryAsync(string unit, CancellationToken cancellationToken)
    {
        string[] arguments = ["show", Checked(unit), "--no-pager", "--property=" + string.Join(',', ShowProperties)];
        CommandResult result = await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new SystemctlException(Describe(arguments, result));
        }

        return ParseShow(unit, result.StandardOutput);
    }

    /// <summary>Parses the output of <c>systemctl show</c>: one <c>Key=Value</c> per line.</summary>
    /// <param name="unit">The unit that was asked about.</param>
    /// <param name="output">The output.</param>
    /// <returns>The state; properties that are missing are empty.</returns>
    public static UnitStatus ParseShow(string unit, string output)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(output);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                values[line[..equals]] = line[(equals + 1)..];
            }
        }

        string Get(string key) => values.TryGetValue(key, out string? value) ? value : string.Empty;

        // A missing or unreadable pid is 0, which stands for "no process".
        _ = int.TryParse(Get("MainPID"), NumberStyles.None, CultureInfo.InvariantCulture, out int mainPid);
        return new UnitStatus(unit, Get("LoadState"), Get("ActiveState"), Get("SubState"), Get("UnitFileState"), mainPid, Get("FragmentPath"));
    }

    // The first character is never '-', so that a name cannot be taken for an option.
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9:_.\\@-]{0,199}\.service$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitNameRegex();

    private static string Checked(string unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return IsValidUnitName(unit) ? unit : throw new ArgumentException($"'{unit}' is not a service unit name.", nameof(unit));
    }

    private static string Describe(IReadOnlyList<string> arguments, CommandResult result)
    {
        string detail = (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim();
        string call = "systemctl " + string.Join(' ', arguments);
        return detail.Length == 0
            ? $"'{call}' failed with exit code {result.ExitCode}."
            : $"'{call}' failed with exit code {result.ExitCode}: {detail}";
    }

    private async Task RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        CommandResult result = await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new SystemctlException(Describe(arguments, result));
        }
    }

    private async Task<CommandResult> ExecuteAsync(string[] arguments, CancellationToken cancellationToken)
    {
        CommandResult? result = await _runner.RunAsync(_fileName, arguments, _timeout, cancellationToken).ConfigureAwait(false);
        return result ?? throw new SystemctlException($"'{_fileName}' could not be run or did not finish in time. Is systemd running on this computer?");
    }
}
