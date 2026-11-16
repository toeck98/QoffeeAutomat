using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Execution;

/// <summary>One supervised process: start, pump the output, enforce the timeout, kill the tree, report.</summary>
internal sealed class ProcessRun : IProcessRun
{
    private const int ReadBufferSize = 8192;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ProcessLaunchRequest _request;
    private readonly TimeProvider _time;
    private readonly ProcessRunnerOptions _options;
    private readonly IProcessContainment _containment;
    private readonly Channel<ProcessOutputChunk> _output = Channel.CreateUnbounded<ProcessOutputChunk>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _cancel;

    public ProcessRun(
        ProcessLaunchRequest request,
        TimeProvider time,
        ProcessRunnerOptions options,
        IProcessContainment containment,
        CancellationToken cancellationToken)
    {
        _request = request;
        _time = time;
        _options = options;
        _containment = containment;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Completion = RunAsync();
    }

    public IAsyncEnumerable<ProcessOutputChunk> Output => _output.Reader.ReadAllAsync();

    public Task<ProcessRunResult> Completion { get; }

    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed after the run ended; nothing to cancel.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        try
        {
            await Completion;
        }
#pragma warning disable CA1031 // A failure of the run is reported to whoever awaits Completion, not by Dispose.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Observed here only so that the task is not left unobserved.
        }

        _cancel.Dispose();
    }

    private static bool IsStartFailure(Exception exception)
    {
        return exception is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException or NotSupportedException or ArgumentException;
    }

    private static int ExitCodeOf(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process is already gone.
        }
    }

    private async Task<ProcessRunResult> RunAsync()
    {
        DateTimeOffset started = _time.GetUtcNow();
        try
        {
            if (_cancel.IsCancellationRequested)
            {
                return new ProcessRunResult(-1, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false, OutputTruncated: false, Cancelled: true);
            }

            return _request.WaitForExit ? await RunAttachedAsync(started) : RunDetached(started);
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    /// <summary>Starts the process and leaves it alone ("start and forget", <c>waitForExit: false</c>).</summary>
    private ProcessRunResult RunDetached(DateTimeOffset started)
    {
        try
        {
            using var process = new Process { StartInfo = CreateStartInfo(new ProcessCommand(_request.FileName, _request.Arguments), detached: true) };
            process.Start();
            int id = process.Id;
            CloseInput(process);
            return new ProcessRunResult(0, string.Empty, string.Empty, Elapsed(started), TimedOut: false, OutputTruncated: false, ProcessId: id);
        }
        catch (Exception exception) when (IsStartFailure(exception))
        {
            return StartFailure(started, exception);
        }
    }

    private async Task<ProcessRunResult> RunAttachedAsync(DateTimeOffset started)
    {
        Process? process = null;
        IProcessTree? tree = null;
        try
        {
            try
            {
                ProcessCommand command = _containment.Prepare(new ProcessCommand(_request.FileName, _request.Arguments));
                process = new Process { StartInfo = CreateStartInfo(command, detached: false) };
                process.Start();
            }
            catch (Exception exception) when (IsStartFailure(exception))
            {
                return StartFailure(started, exception);
            }

            tree = Attach(process);
            CloseInput(process);

            OutputCapture? stdout = _request.CaptureOutput ? new OutputCapture(_options.OutputLimitBytes) : null;
            OutputCapture? stderr = _request.CaptureOutput ? new OutputCapture(_options.OutputLimitBytes) : null;
            Task stdoutPump = PumpAsync(process.StandardOutput.BaseStream, ProcessOutputSource.StandardOutput, stdout);
            Task stderrPump = PumpAsync(process.StandardError.BaseStream, ProcessOutputSource.StandardError, stderr);

            bool timedOut = false;
            bool cancelled = false;
            using (var timeout = new CancellationTokenSource(_request.Timeout, _time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token, timeout.Token))
            {
                try
                {
                    await process.WaitForExitAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = _cancel.IsCancellationRequested;
                    timedOut = !cancelled;
                    tree.Kill();
                    await WaitAfterKillAsync(process);
                }
            }

            await DrainAsync(stdoutPump, stderrPump);

            return new ProcessRunResult(
                ExitCodeOf(process),
                stdout?.Text ?? string.Empty,
                stderr?.Text ?? string.Empty,
                Elapsed(started),
                timedOut,
                (stdout?.IsTruncated ?? false) || (stderr?.IsTruncated ?? false),
                cancelled,
                StartError: null,
                ProcessId: process.Id);
        }
        finally
        {
            tree?.Dispose();
            process?.Dispose();
        }
    }

    private IProcessTree Attach(Process process)
    {
        try
        {
            return _containment.Attach(process);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or IOException)
        {
            // A containment that cannot take hold (for example no permission to create a job
            // object) must not stop the run; killing by walking the tree still works.
            return new ProcessTreeContainment().Attach(process);
        }
    }

    private ProcessStartInfo CreateStartInfo(ProcessCommand command, bool detached)
    {
        var info = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = !detached,
            RedirectStandardError = !detached,
        };

        if (!detached)
        {
            // The framework rejects an output encoding when the stream is not redirected.
            info.StandardOutputEncoding = Utf8;
            info.StandardErrorEncoding = Utf8;
        }

        // One entry per argv element; the framework quotes for the platform. Nothing is spliced or split.
        foreach (string argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (_request.WorkingDirectory is { Length: > 0 } directory)
        {
            info.WorkingDirectory = directory;
        }

        foreach ((string name, string value) in _request.EnvironmentVariables)
        {
            info.Environment[name] = value;
        }

        return info;
    }

    /// <summary>Reads a stream to its end. Everything beyond the cap is read and dropped so the process never blocks on a full pipe.</summary>
    private async Task PumpAsync(Stream stream, ProcessOutputSource source, OutputCapture? capture)
    {
        byte[] buffer = new byte[ReadBufferSize];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                if (capture?.Append(buffer.AsSpan(0, read)) is { Length: > 0 } text)
                {
                    _output.Writer.TryWrite(new ProcessOutputChunk(source, text, _time.GetUtcNow()));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The stream was closed under us (the process object was disposed after a stuck run).
        }
    }

    private async Task WaitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(_options.KillWaitTimeout, _time);
        }
        catch (TimeoutException)
        {
            // The process survived the kill; report what is known.
        }
    }

    /// <summary>
    /// Waits until both streams reached their end. A descendant that outlives the process and keeps
    /// the pipes open only delays the result by <see cref="ProcessRunnerOptions.OutputDrainTimeout"/>.
    /// </summary>
    private async Task DrainAsync(Task stdoutPump, Task stderrPump)
    {
        try
        {
            await Task.WhenAll(stdoutPump, stderrPump).WaitAsync(_options.OutputDrainTimeout, _time);
        }
        catch (TimeoutException)
        {
            // Output that arrives later is not part of the result.
        }
    }

    private ProcessRunResult StartFailure(DateTimeOffset started, Exception exception)
    {
        return new ProcessRunResult(
            -1,
            string.Empty,
            string.Empty,
            Elapsed(started),
            TimedOut: false,
            OutputTruncated: false,
            StartError: $"Could not start '{_request.FileName}': {exception.Message}");
    }

    private TimeSpan Elapsed(DateTimeOffset started)
    {
        TimeSpan elapsed = _time.GetUtcNow() - started;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }
}
