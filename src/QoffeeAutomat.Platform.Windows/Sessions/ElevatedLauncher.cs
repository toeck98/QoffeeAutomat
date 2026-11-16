using System.ComponentModel;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Security;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>Settings of <see cref="ElevatedLauncher"/>.</summary>
public sealed class ElevatedLauncherOptions
{
    /// <summary>The cap per stream: 64 KiB (PLAN.md S06, <c>captureOutput</c>).</summary>
    public int OutputLimitBytes { get; init; } = 64 * 1024;

    /// <summary>After the process ended, how long to wait for its output to reach the end; a descendant that keeps the pipes open cannot hold the result back longer.</summary>
    public TimeSpan OutputDrainTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>After the kill, how long to wait for the process to be gone before the result is reported anyway.</summary>
    public TimeSpan KillWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Starts processes for <c>runAs: user_elevated</c> (PLAN.md S11, D-036, T311). The agent takes the token of the active console
/// session, its linked elevated token and the environment block of the user, starts the process with
/// <c>CreateProcessAsUser</c> on the interactive desktop, captures its output through pipes that it owns and enforces the
/// timeout and cancellation with a job object, so a program that requires administrator rights starts on the desktop of
/// the user without a UAC prompt. Nobody logged on is reported as <c>no_session</c>; a user without an elevated linked token
/// (a standard user, or UAC switched off) as <c>elevation_unavailable</c>, and then nothing is started.
/// </summary>
/// <remarks>
/// The service already runs as LocalSystem, so this hands out no right that <c>runAs: system</c> does not have already
/// (D-036). A PowerShell action arrives as the engine with the wrapper script as its argument and the parameter file in the
/// environment (D-012); before the process starts, the private directory of that run is restricted to SYSTEM,
/// Administrators and the interactive user, and a run is refused when that cannot be done.
/// </remarks>
public sealed class ElevatedLauncher : IElevatedLauncher
{
    private const int ReadBufferSize = 8192;
    private const FileSystemRights ReadAndExecute = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    private static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(40);

    private readonly IElevatedNativeApi _native;
    private readonly IFileSystemAcl _acl;
    private readonly TimeProvider _time;
    private readonly ElevatedLauncherOptions _options;
    private readonly ILogger? _logger;

    /// <summary>Creates the launcher.</summary>
    /// <param name="native">The Windows calls; tests pass a fake.</param>
    /// <param name="acl">Restricts the directory of a PowerShell run.</param>
    /// <param name="time">The clock for the timeout; null uses the system clock.</param>
    /// <param name="options">Tuning; null uses the defaults.</param>
    /// <param name="logger">Receives a message for every elevated start (the audit trail is the run history); null logs nothing.</param>
    public ElevatedLauncher(
        IElevatedNativeApi native,
        IFileSystemAcl acl,
        TimeProvider? time = null,
        ElevatedLauncherOptions? options = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(acl);

        _native = native;
        _acl = acl;
        _time = time ?? TimeProvider.System;
        _options = options ?? new ElevatedLauncherOptions();
        _logger = logger;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.OutputLimitBytes, nameof(options));
    }

    /// <inheritdoc />
    public async ValueTask<ExecutionResult> StartAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.FileName) || request.Arguments is null || request.EnvironmentVariables is null)
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, "The request does not name a program.");
        }

        if (request.WaitForExit && (request.Timeout <= TimeSpan.Zero || request.Timeout > MaxTimeout))
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, "The timeout must be positive and at most 40 days.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ExecutionResult(-1, string.Empty, string.Empty, ErrorCodes.Cancelled);
        }

        IElevatedToken? token = null;
        try
        {
            ExecutionResult? unavailable = OpenToken(out token);
            if (unavailable is not null)
            {
                return unavailable;
            }

            ExecutionResult? refused = ProtectPowerShellWorkspace(request, token!);
            if (refused is not null)
            {
                return refused;
            }

            IElevatedProcess process;
            try
            {
                process = _native.Start(token!, new ElevatedLaunch(
                    request.FileName,
                    request.Arguments,
                    request.WorkingDirectory,
                    request.EnvironmentVariables,
                    Attached: request.WaitForExit));
            }
            catch (Exception exception) when (exception is ElevatedLaunchException or Win32Exception or IOException or InvalidOperationException)
            {
                return ExecutionResult.Failure(ErrorCodes.Internal, $"Could not start '{request.FileName}' with administrator rights: {exception.Message}");
            }
            catch (ArgumentException exception)
            {
                // A variable that an environment block cannot hold, for example a NUL character in a value.
                return ExecutionResult.Failure(ErrorCodes.InvalidVars, exception.Message);
            }

            using (process)
            {
                if (_logger is not null)
                {
                    ElevatedLog.Started(_logger, request.FileName, token!.UserName ?? token.UserSid, process.ProcessId);
                }

                return request.WaitForExit
                    ? await SuperviseAsync(process, request, cancellationToken).ConfigureAwait(false)
                    : new ExecutionResult(0, string.Empty, string.Empty);
            }
        }
        finally
        {
            token?.Dispose();
        }
    }

    private static ExecutionResult TokenFailure(ElevatedTokenResult result)
    {
        return result.Status switch
        {
            ElevatedTokenStatus.NoSession => ExecutionResult.Failure(
                ErrorCodes.NoSession,
                result.Message ?? "Nobody is logged in at the console of this computer."),
            ElevatedTokenStatus.NoLinkedToken => ExecutionResult.Failure(
                ErrorCodes.ElevationUnavailable,
                result.Message ?? "The logged-in user has no elevated token: the user is not an administrator, or User Account Control is switched off."),
            _ => ExecutionResult.Failure(
                ErrorCodes.Internal,
                result.Message ?? "The token of the logged-in user could not be obtained."),
        };
    }

    /// <summary>Finds the console session and its elevated token; returns the failure to report when there is none.</summary>
    private ExecutionResult? OpenToken(out IElevatedToken? token)
    {
        token = null;
        try
        {
            if (_native.GetActiveConsoleSessionId() is not { } sessionId)
            {
                return ExecutionResult.Failure(ErrorCodes.NoSession, "Nobody is logged in at the console of this computer.");
            }

            ElevatedTokenResult result = _native.OpenElevatedToken(sessionId);
            if (result.Status != ElevatedTokenStatus.Available || result.Token is null)
            {
                return TokenFailure(result);
            }

            token = result.Token;
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, "The token of the logged-in user could not be obtained: " + exception.Message);
        }
    }

    /// <summary>
    /// A PowerShell run keeps its wrapper, parameter file and inline code in a private directory of the agent. The elevated
    /// process is the user, who is not allowed there yet: grant SYSTEM, Administrators and that user, and nobody else.
    /// </summary>
    private ExecutionResult? ProtectPowerShellWorkspace(ProcessLaunchRequest request, IElevatedToken token)
    {
        if (!request.EnvironmentVariables.TryGetValue(PowerShellWrapper.ParametersFileVariable, out string? parameterFile)
            || string.IsNullOrWhiteSpace(parameterFile))
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(parameterFile));
        if (directory is null
            || !Path.GetFileName(directory).StartsWith(PowerShellRunner.WorkspacePrefix, StringComparison.Ordinal)
            || !Directory.Exists(directory))
        {
            // Not a workspace of the PowerShell runner: nothing of ours to protect, and no business changing other directories.
            return null;
        }

        try
        {
            _acl.Protect(directory,
            [
                new AclGrant(WellKnownAccounts.LocalSystem, FileSystemRights.FullControl),
                new AclGrant(WellKnownAccounts.Administrators, FileSystemRights.FullControl),
                new AclGrant(token.UserSid, ReadAndExecute),
            ]);
            return null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
        {
            return ExecutionResult.Failure(
                ErrorCodes.Internal,
                "The script was not started because its parameter file could not be restricted to the administrators and the user: " + exception.Message);
        }
    }

    private async ValueTask<ExecutionResult> SuperviseAsync(IElevatedProcess process, ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        var stdout = request.CaptureOutput ? new CappedTextCapture(_options.OutputLimitBytes) : null;
        var stderr = request.CaptureOutput ? new CappedTextCapture(_options.OutputLimitBytes) : null;
        Task stdoutPump = PumpAsync(process.StandardOutput, ProcessOutputSource.StandardOutput, stdout, request.OnOutput);
        Task stderrPump = PumpAsync(process.StandardError, ProcessOutputSource.StandardError, stderr, request.OnOutput);

        bool timedOut = false;
        bool cancelled = false;
        int exitCode;
        using (var timeout = new CancellationTokenSource(request.Timeout, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
        {
            try
            {
                exitCode = await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = cancellationToken.IsCancellationRequested;
                timedOut = !cancelled;
                process.Kill();
                exitCode = await WaitAfterKillAsync(process).ConfigureAwait(false);
            }
        }

        await DrainAsync(stdoutPump, stderrPump).ConfigureAwait(false);

        string? errorCode = timedOut ? ErrorCodes.Timeout : cancelled ? ErrorCodes.Cancelled : null;
        return new ExecutionResult(
            exitCode,
            stdout?.Text ?? string.Empty,
            stderr?.Text ?? string.Empty,
            errorCode,
            (stdout?.IsTruncated ?? false) || (stderr?.IsTruncated ?? false));
    }

    private async Task<int> WaitAfterKillAsync(IElevatedProcess process)
    {
        try
        {
            return await process.WaitForExitAsync(CancellationToken.None).WaitAsync(_options.KillWaitTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The process survived the kill; report what is known.
            return -1;
        }
    }

    /// <summary>Reads a stream to its end. Everything beyond the cap is read and dropped so the process never blocks on a full pipe.</summary>
    private async Task PumpAsync(Stream? stream, ProcessOutputSource source, CappedTextCapture? capture, Action<ProcessOutputChunk>? live)
    {
        if (stream is null)
        {
            return;
        }

        byte[] buffer = new byte[ReadBufferSize];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                if (capture?.Append(buffer.AsSpan(0, read)) is { Length: > 0 } text && live is not null)
                {
#pragma warning disable CA1031 // The receiver is a best-effort live view; the captured result is unaffected.
                    try
                    {
                        live(new ProcessOutputChunk(source, text, _time.GetUtcNow()));
                    }
                    catch (Exception)
                    {
                        // Ignored on purpose.
                    }
#pragma warning restore CA1031
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The pipe was closed under us (the process was disposed after a stuck run).
        }
    }

    private async Task DrainAsync(Task stdoutPump, Task stderrPump)
    {
        try
        {
            await Task.WhenAll(stdoutPump, stderrPump).WaitAsync(_options.OutputDrainTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Output that arrives later is not part of the result.
        }
    }
}

/// <summary>
/// Keeps the first <c>limit</c> bytes of a process stream as UTF-8 text and remembers that more arrived. A multi-byte
/// character cut in two by the limit is dropped, not replaced.
/// </summary>
internal sealed class CappedTextCapture
{
    private readonly object _gate = new();
    private readonly int _limit;
    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();
    private readonly StringBuilder _text = new();
    private int _bytes;
    private bool _truncated;

    public CappedTextCapture(int limit)
    {
        _limit = limit;
    }

    public bool IsTruncated
    {
        get
        {
            lock (_gate)
            {
                return _truncated;
            }
        }
    }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    /// <summary>Adds bytes read from the stream.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The text that was kept, or null when nothing was (the limit was reached).</returns>
    public string? Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            int room = _limit - _bytes;
            if (data.Length > room)
            {
                _truncated = true;
            }

            if (room <= 0 || data.IsEmpty)
            {
                return null;
            }

            ReadOnlySpan<byte> keep = data.Length <= room ? data : data[..room];
            _bytes += keep.Length;

            char[] chars = new char[_decoder.GetCharCount(keep, flush: false)];
            int count = _decoder.GetChars(keep, chars, flush: false);
            string text = new(chars, 0, count);
            _text.Append(text);
            return text;
        }
    }
}

/// <summary>Source-generated log messages of the elevated launcher (event ids 340 to 349).</summary>
internal static partial class ElevatedLog
{
    [LoggerMessage(EventId = 340, Level = LogLevel.Information, Message = "Started '{File}' with administrator rights for {User} (process {ProcessId}).")]
    public static partial void Started(ILogger logger, string file, string user, int processId);
}
