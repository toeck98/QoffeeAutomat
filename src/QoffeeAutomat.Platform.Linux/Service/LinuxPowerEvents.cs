using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>The signals of logind that announce a power transition.</summary>
public enum LogindSignalKind
{
    /// <summary><c>PrepareForSleep</c>: the computer is about to suspend (<c>Starting</c>) or has resumed.</summary>
    PrepareForSleep,

    /// <summary><c>PrepareForShutdown</c>: the computer is about to shut down or restart (<c>Starting</c>), or the shutdown was cancelled.</summary>
    PrepareForShutdown,
}

/// <summary>One signal of <c>org.freedesktop.login1.Manager</c>.</summary>
/// <param name="Kind">Which signal.</param>
/// <param name="Starting">The boolean argument of the signal: true when the transition begins, false when it is over or cancelled.</param>
public readonly record struct LogindSignal(LogindSignalKind Kind, bool Starting);

/// <summary>Delivers the power signals of logind (PLAN.md S17, T802).</summary>
public interface ILogindSignalSource
{
    /// <summary>Listens for the signals until the token is cancelled or the connection to logind ends.</summary>
    /// <param name="cancellationToken">Ends the listening.</param>
    /// <returns>The signals, in the order logind sent them; the sequence ends when logind cannot be listened to any more.</returns>
    IAsyncEnumerable<LogindSignal> WatchAsync(CancellationToken cancellationToken);
}

/// <summary>Holds a delay inhibitor lock of logind (PLAN.md S17, T802): logind waits for the holder (at most <c>InhibitDelayMaxSec</c>, five seconds by default) before it suspends or shuts down.</summary>
public interface ILogindInhibitor
{
    /// <summary>Takes a lock that delays sleep and shutdown.</summary>
    /// <param name="reason">Why the lock is held; shown by <c>systemd-inhibit --list</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The lock; disposing it lets the transition go on. Null when logind refused or cannot be reached.</returns>
    ValueTask<IDisposable?> AcquireDelayLockAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>
/// Listens to logind with <c>busctl monitor</c>, a program that every system with systemd has (PLAN.md S17, T802). The agent needs no D-Bus
/// library for this: the signals are two matches on the system bus, and <c>busctl --json=short</c> prints one JSON object per message.
/// Monitoring the system bus needs the rights of root, which the service has; as an ordinary user the sequence ends at once.
/// </summary>
public sealed class BusctlLogindSignalSource : ILogindSignalSource
{
    private const string Interface = "org.freedesktop.login1.Manager";

    private readonly string _fileName;

    /// <summary>Creates the source.</summary>
    /// <param name="fileName">The program; <c>busctl</c> is looked up in <c>PATH</c>.</param>
    public BusctlLogindSignalSource(string fileName = "busctl")
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        _fileName = fileName;
    }

    /// <summary>The arguments of <c>busctl</c>: the two matches and the verb.</summary>
    /// <returns>One entry per argv element.</returns>
    public static IReadOnlyList<string> BuildArguments()
    {
        return
        [
            "--system",
            "--json=short",
            $"--match=type='signal',interface='{Interface}',member='PrepareForSleep'",
            $"--match=type='signal',interface='{Interface}',member='PrepareForShutdown'",
            "monitor",
        ];
    }

    /// <summary>Reads a signal from one line of the output of <c>busctl --json=short monitor</c>.</summary>
    /// <param name="line">The line: a JSON object with <c>member</c> and <c>payload.data</c>.</param>
    /// <returns>The signal; null for any other message, for the line that busctl prints when it attaches, and for text that is not JSON.</returns>
    public static LogindSignal? ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Length == 0 || line[0] != '{')
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("member", out JsonElement member)
                || member.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("payload", out JsonElement payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() < 1)
            {
                return null;
            }

            JsonElement argument = data[0];
            if (argument.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            LogindSignalKind? kind = member.GetString() switch
            {
                "PrepareForSleep" => LogindSignalKind.PrepareForSleep,
                "PrepareForShutdown" => LogindSignalKind.PrepareForShutdown,
                _ => null,
            };
            return kind is { } known ? new LogindSignal(known, argument.GetBoolean()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LogindSignal> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in BuildArguments())
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process? process = TryStart(startInfo);
        if (process is null)
        {
            yield break;
        }

        try
        {
            process.StandardInput.Close();

            // Standard error is read so that the program never blocks on a full pipe; its messages are not needed.
            Task errors = process.StandardError.ReadToEndAsync(cancellationToken);
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (ParseLine(line) is { } signal)
                {
                    yield return signal;
                }
            }

            await errors.ConfigureAwait(false);
        }
        finally
        {
            Kill(process);
        }
    }

    private static Process? TryStart(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            // busctl is not installed: there are no signals to deliver.
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The program ended on its own in the meantime.
        }
    }
}

/// <summary>
/// Takes the delay lock with <c>systemd-inhibit</c> (PLAN.md S17, T802): the program holds the lock while the command it runs, <c>cat</c>, is
/// alive, and <c>cat</c> ends when its standard input is closed. Disposing the lock closes the input, and so does the death of the
/// agent, so a crashed agent never leaves the computer unable to suspend. A real D-Bus call is not an option without a library: logind hands the
/// lock over as a file descriptor, which needs <c>SCM_RIGHTS</c>.
/// </summary>
public sealed class SystemdInhibitor : ILogindInhibitor
{
    private static readonly TimeSpan StartupCheck = TimeSpan.FromMilliseconds(400);

    private readonly string _fileName;

    /// <summary>Creates the inhibitor.</summary>
    /// <param name="fileName">The program; <c>systemd-inhibit</c> is looked up in <c>PATH</c>.</param>
    public SystemdInhibitor(string fileName = "systemd-inhibit")
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        _fileName = fileName;
    }

    /// <summary>The arguments of <c>systemd-inhibit</c>.</summary>
    /// <param name="reason">The text for <c>--why</c>.</param>
    /// <returns>One entry per argv element.</returns>
    public static IReadOnlyList<string> BuildArguments(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return ["--what=sleep:shutdown", "--who=QoffeeAutomat", "--why=" + reason, "--mode=delay", "cat"];
    }

    /// <inheritdoc />
    public async ValueTask<IDisposable?> AcquireDelayLockAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var startInfo = new ProcessStartInfo(_fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in BuildArguments(reason))
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        // A refused lock ends the program at once with an error; a granted lock keeps it alive.
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(StartupCheck);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            process.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProcessLock(process);
        }
        catch (OperationCanceledException)
        {
            process.Dispose();
            throw;
        }
    }

    private sealed class ProcessLock : IDisposable
    {
        private readonly Process _process;

        public ProcessLock(Process process)
        {
            _process = process;
        }

        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(TimeSpan.FromSeconds(2)))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException)
            {
                // The program ended already, which is what releasing the lock means.
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}

/// <summary>Tuning of <see cref="LinuxPowerEvents"/>.</summary>
public sealed record LinuxPowerEventsOptions
{
    /// <summary>How long after <see cref="LinuxPowerEvents.ExpectHibernate"/> or <see cref="LinuxPowerEvents.ExpectRestart"/> the hint applies.</summary>
    public TimeSpan HintLifetime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait before the connection to logind is opened again after it ended.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The text of the lock that logind shows.</summary>
    public string LockReason { get; init; } = "Telling Home Assistant that this computer goes away";
}

/// <summary>
/// The <see cref="IPowerEvents"/> of Linux (PLAN.md S07, S17, T802). The agent holds a delay lock of logind. When logind announces a
/// suspend or shutdown (<c>PrepareForSleep</c> and <c>PrepareForShutdown</c>), the handlers run synchronously, which is what the QoffeeLink
/// server needs: <c>bye</c> must be written (and flushed) before the lock is released, because the computer goes on as soon as it is
/// (compare <c>WindowsPowerEvents</c>). When the computer wakes up, or a shutdown is cancelled, the lock is taken again.
/// </summary>
/// <remarks>
/// logind reports neither hibernate (it is a suspend) nor restart (it is a shutdown) as such. The agent knows when it starts one of them
/// itself and says so with <see cref="ExpectHibernate"/> or <see cref="ExpectRestart"/>; the hint is valid for
/// <see cref="LinuxPowerEventsOptions.HintLifetime"/>. A hibernate or restart started by someone else is reported as sleep or shutdown.
/// The lock lasts as long as <c>InhibitDelayMaxSec</c> of logind allows (five seconds); after that the computer goes on without waiting.
/// </remarks>
public sealed partial class LinuxPowerEvents : IPowerEvents, IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly ILogindSignalSource _signals;
    private readonly ILogindInhibitor _inhibitor;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly LinuxPowerEventsOptions _options;
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private IDisposable? _lock;
    private PowerEventKind? _hint;
    private DateTimeOffset _hintAt;

    /// <summary>Creates the source.</summary>
    /// <param name="signals">Delivers the signals of logind.</param>
    /// <param name="inhibitor">Takes the delay lock.</param>
    /// <param name="logger">Receives the messages about logind.</param>
    /// <param name="time">The clock for the hints and the reconnect delay; null uses the system clock.</param>
    /// <param name="options">Tuning; null uses the defaults.</param>
    public LinuxPowerEvents(
        ILogindSignalSource signals,
        ILogindInhibitor inhibitor,
        ILogger logger,
        TimeProvider? time = null,
        LinuxPowerEventsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(inhibitor);
        ArgumentNullException.ThrowIfNull(logger);

        _signals = signals;
        _inhibitor = inhibitor;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _options = options ?? new LinuxPowerEventsOptions();
    }

    /// <inheritdoc />
    public event EventHandler<PowerEventArgs>? PowerChanged;

    /// <summary>True while the delay lock is held.</summary>
    public bool HoldsLock
    {
        get
        {
            lock (_gate)
            {
                return _lock is not null;
            }
        }
    }

    /// <summary>Tells that the agent is about to hibernate this computer, so the next suspend is reported as hibernate.</summary>
    public void ExpectHibernate() => SetHint(PowerEventKind.Hibernate);

    /// <summary>Tells that the agent is about to restart this computer, so the next shutdown is reported as restart.</summary>
    public void ExpectRestart() => SetHint(PowerEventKind.Restart);

    /// <summary>Takes the delay lock and starts listening to logind. A computer without logind is no error: there is nothing to announce.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task that completes when the lock is taken or refused; it does not wait for the signals.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var stop = new CancellationTokenSource();
        _stop = stop;
        _loop = Task.Run(() => ListenAsync(stop.Token), CancellationToken.None);
    }

    /// <summary>Stops listening and releases the lock.</summary>
    /// <param name="cancellationToken">Gives up waiting for the listener.</param>
    /// <returns>A task that completes when the listener has ended.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stop is not null)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The caller does not wait any longer; the listener ends on its own.
            }
        }

        ReleaseLock();
    }

    /// <summary>
    /// Handles one signal. The handlers of <see cref="PowerChanged"/> run before the lock is released, so the clients are told <c>bye</c> before
    /// the computer goes away. The host calls it for every signal that the source delivers.
    /// </summary>
    /// <param name="signal">The signal.</param>
    /// <param name="cancellationToken">Cancels taking the lock again.</param>
    /// <returns>A task that completes when the signal is handled.</returns>
    public async ValueTask HandleAsync(LogindSignal signal, CancellationToken cancellationToken = default)
    {
        switch (signal)
        {
            case { Kind: LogindSignalKind.PrepareForSleep, Starting: true }:
                Raise(TakeHint(PowerEventKind.Hibernate) ? PowerEventKind.Hibernate : PowerEventKind.Sleep);
                ReleaseLock();
                break;
            case { Kind: LogindSignalKind.PrepareForShutdown, Starting: true }:
                Raise(TakeHint(PowerEventKind.Restart) ? PowerEventKind.Restart : PowerEventKind.Shutdown);
                ReleaseLock();
                break;
            case { Kind: LogindSignalKind.PrepareForSleep }:
                await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
                Raise(PowerEventKind.Resume);
                break;
            default:
                // The shutdown was cancelled: the computer stays, so the lock is held again.
                await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Stops listening and releases the lock.</summary>
    public void Dispose()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        ReleaseLock();
    }

    [LoggerMessage(EventId = 3200, Level = LogLevel.Information, Message = "The delay lock of logind is held: bye is sent before the computer sleeps or shuts down.")]
    private static partial void LockHeld(ILogger logger);

    [LoggerMessage(EventId = 3201, Level = LogLevel.Information, Message = "logind did not grant a delay lock (systemd-inhibit is missing or refused); the clients are not told before sleep or shutdown.")]
    private static partial void LockRefused(ILogger logger);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Information, Message = "The power signals of logind are not available (busctl is missing, or the agent has no access to the system bus); trying again in {Seconds} s.")]
    private static partial void SignalsUnavailable(ILogger logger, double seconds);

    [LoggerMessage(EventId = 3203, Level = LogLevel.Warning, Message = "A listener of the power event {Kind} failed: {Reason}")]
    private static partial void HandlerFailed(ILogger logger, PowerEventKind kind, string reason);

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        bool reported = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (LogindSignal signal in _signals.WatchAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    await HandleAsync(signal, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // The first time is worth a message; a computer without access to the bus would repeat it forever.
            if (!reported)
            {
                SignalsUnavailable(_logger, _options.ReconnectDelay.TotalSeconds);
                reported = true;
            }

            try
            {
                await Task.Delay(_options.ReconnectDelay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task AcquireLockAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_lock is not null)
            {
                return;
            }
        }

        IDisposable? acquired = await _inhibitor.AcquireDelayLockAsync(_options.LockReason, cancellationToken).ConfigureAwait(false);
        if (acquired is null)
        {
            LockRefused(_logger);
            return;
        }

        bool kept;
        lock (_gate)
        {
            kept = _lock is null;
            if (kept)
            {
                _lock = acquired;
            }
        }

        if (kept)
        {
            LockHeld(_logger);
        }
        else
        {
            acquired.Dispose();
        }
    }

    private void ReleaseLock()
    {
        IDisposable? held;
        lock (_gate)
        {
            held = _lock;
            _lock = null;
        }

        held?.Dispose();
    }

    private void SetHint(PowerEventKind kind)
    {
        lock (_gate)
        {
            _hint = kind;
            _hintAt = _time.GetUtcNow();
        }
    }

    private bool TakeHint(PowerEventKind kind)
    {
        lock (_gate)
        {
            bool fresh = _time.GetUtcNow() - _hintAt <= _options.HintLifetime;
            bool matches = _hint == kind && fresh;

            // A hint is used up by the event it announced, and an old one is dropped.
            if (matches || !fresh)
            {
                _hint = null;
            }

            return matches;
        }
    }

    private void Raise(PowerEventKind kind)
    {
        EventHandler<PowerEventArgs>? handlers = PowerChanged;
        if (handlers is null)
        {
            return;
        }

        // One failing subscriber must not keep the others from saying goodbye before the computer sleeps.
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<PowerEventArgs>)handler)(this, new PowerEventArgs(kind));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                HandlerFailed(_logger, kind, exception.Message);
            }
        }
    }
}
