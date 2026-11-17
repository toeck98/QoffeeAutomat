using System.Globalization;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.SessionHost.Audio;

/// <summary>
/// The audio devices of the session (PLAN.md S06, S11, T713): which playback and recording devices are active and which one is
/// the default (the metrics <c>audio.output_device</c> and <c>audio.input_device</c>), changing the default device (the actions
/// <c>audio.set_default_output</c> and <c>audio.set_default_input</c>), and the notifications that keep the metrics and their options
/// up to date. All Windows calls are behind <see cref="IAudioEndpointCatalog"/> and <see cref="IDefaultEndpointSwitch"/>, so the
/// logic here runs in tests with fakes.
/// </summary>
/// <remarks>
/// Nothing is watched until somebody calls <see cref="Watch"/> (the session client does while it is connected to the agent), and
/// the notifications are given back when the last watcher goes away (D-007). A change that does not alter the labels of the
/// devices or the default one (a volume change, a property that was rewritten) is not reported, so the burst of notifications that
/// one plugged-in device produces gives one message for each metric that really changed.
/// </remarks>
public sealed class AudioDeviceService : IMetricChangeSource
{
    private static readonly AudioRole[] Roles = [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications];

    private readonly object _gate = new();
    private readonly IAudioEndpointCatalog _catalog;
    private readonly IDefaultEndpointSwitch _switch;
    private readonly ISessionHostLog _log;
    private readonly List<Watcher> _watchers = [];
    private IDisposable? _subscription;
    private AudioSnapshot _lastOutput = AudioSnapshot.Empty;
    private AudioSnapshot _lastInput = AudioSnapshot.Empty;

    /// <summary>Creates the service.</summary>
    /// <param name="catalog">Lists the devices and reports changes.</param>
    /// <param name="defaultSwitch">Changes the default device.</param>
    /// <param name="log">The diagnostics log.</param>
    public AudioDeviceService(IAudioEndpointCatalog catalog, IDefaultEndpointSwitch defaultSwitch, ISessionHostLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(defaultSwitch);

        _catalog = catalog;
        _switch = defaultSwitch;
        _log = log ?? NullSessionHostLog.Instance;
    }

    /// <summary>Creates the service that really acts on this computer.</summary>
    /// <param name="log">The diagnostics log.</param>
    /// <returns>The service.</returns>
    public static AudioDeviceService CreateNative(ISessionHostLog? log = null)
    {
        return new AudioDeviceService(new WindowsAudioEndpointCatalog(log), new PolicyConfigEndpointSwitch(), log);
    }

    /// <summary>The metric that reports the devices of a direction.</summary>
    /// <param name="flow">The direction.</param>
    /// <returns>The metric id.</returns>
    public static string MetricIdOf(AudioFlow flow)
    {
        return flow == AudioFlow.Output ? SessionMetricIds.AudioOutputDevice : SessionMetricIds.AudioInputDevice;
    }

    /// <summary>Reads the devices of one direction.</summary>
    /// <param name="flow">The direction.</param>
    /// <returns>The active devices and the default one; empty when there is no audio device or the audio service does not answer. The method does not throw.</returns>
    public AudioSnapshot Read(AudioFlow flow)
    {
#pragma warning disable CA1031 // A catalog that fails means "no devices": the metrics are unavailable, nothing else breaks.
        try
        {
            IReadOnlyList<AudioDevice> devices = AudioDeviceMatcher.Label(_catalog.GetActiveEndpoints(flow));
            string? defaultId = _catalog.GetDefaultEndpointId(flow);
            if (defaultId is not null && !devices.Any(device => string.Equals(device.Id, defaultId, StringComparison.Ordinal)))
            {
                // The default device is one that is not active (it was just unplugged): there is no usable default.
                defaultId = null;
            }

            return new AudioSnapshot(devices, defaultId);
        }
        catch (Exception)
        {
            return AudioSnapshot.Empty;
        }
#pragma warning restore CA1031
    }

    /// <summary>The label of the default device of a direction, the value of the metric.</summary>
    /// <param name="flow">The direction.</param>
    /// <returns>The label; null when there is no default device.</returns>
    public string? CurrentLabel(AudioFlow flow) => Read(flow).DefaultLabel;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CurrentOptions()
    {
        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [SessionMetricIds.AudioOutputDevice] = Read(AudioFlow.Output).Labels,
            [SessionMetricIds.AudioInputDevice] = Read(AudioFlow.Input).Labels,
        };
    }

    /// <inheritdoc />
    public IDisposable Watch(Action<MetricChangeNotice> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);

        lock (_gate)
        {
            var watcher = new Watcher(this, onChange);
            _watchers.Add(watcher);
            if (_watchers.Count == 1)
            {
                _lastOutput = Read(AudioFlow.Output);
                _lastInput = Read(AudioFlow.Input);
#pragma warning disable CA1031 // Without notifications the metrics still follow the sampling interval; the session must not fail.
                try
                {
                    _subscription = _catalog.Subscribe(OnCatalogChanged);
                }
                catch (Exception exception)
                {
                    _subscription = null;
                    _log.Warning("The audio device notifications could not be started: " + exception.Message);
                }
#pragma warning restore CA1031
            }

            return watcher;
        }
    }

    /// <summary>
    /// Makes a device the default device (and the default communication device) (<c>audio.set_default_output</c> and
    /// <c>audio.set_default_input</c>). The text is matched first, and an ambiguous or unknown name is refused with
    /// <see cref="ErrorCodes.InvalidVars"/> before anything is changed.
    /// </summary>
    /// <param name="flow">The direction.</param>
    /// <param name="device">The variable <c>Device</c>: a label or a part of one.</param>
    /// <returns>Success; <see cref="ErrorCodes.InvalidVars"/> for a name that fits no device or several; a failed result with a message when Windows refused.</returns>
    public ExecutionResult SetDefault(AudioFlow flow, string? device)
    {
        string kind = flow == AudioFlow.Output ? "playback" : "recording";
        string text = device?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Invalid("Device is required: the name of a " + kind + " device or a part of it.");
        }

        if (text.Length > AudioDeviceSpec.MaxDeviceLength)
        {
            return Invalid("Device may have at most " + AudioDeviceSpec.MaxDeviceLength.ToString(CultureInfo.InvariantCulture) + " characters.");
        }

        AudioSnapshot snapshot = Read(flow);
        AudioMatch match = AudioDeviceMatcher.Match(snapshot.Devices, text);
        switch (match.Kind)
        {
            case AudioMatchKind.NotFound:
                return Invalid(snapshot.Devices.Count == 0
                    ? "There is no active " + kind + " device."
                    : "No active " + kind + " device has '" + text + "' in its name. Active devices: " + Names(snapshot.Labels) + ".");
            case AudioMatchKind.Ambiguous:
                return Invalid("'" + text + "' fits several " + kind + " devices: " + Names(match.Candidates) + ". Use the whole name or a longer part of it.");
            case AudioMatchKind.Empty:
                return Invalid("Device is required: the name of a " + kind + " device or a part of it.");
            default:
                break;
        }

        AudioDevice target = match.Device!;
        for (int index = 0; index < Roles.Length; index++)
        {
            AudioRole role = Roles[index];
#pragma warning disable CA1031 // Whatever the undocumented interface does wrong, the action fails with a message instead of ending the session helper.
            try
            {
                _switch.SetDefault(target.Id, role);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                string earlier = index == 0 ? string.Empty : " The earlier roles may already have changed.";
                return new ExecutionResult(
                    1,
                    string.Empty,
                    "The default " + kind + " device could not be changed to '" + target.Label + "' (" + RoleName(role) + "): " + exception.Message + earlier);
            }
#pragma warning restore CA1031
        }

        return new ExecutionResult(0, "The default " + kind + " device is now '" + target.Label + "' (also the default communication device).", string.Empty);
    }

    private static ExecutionResult Invalid(string message) => ExecutionResult.Failure(ErrorCodes.InvalidVars, message);

    private static string Names(IEnumerable<string> labels) => string.Join(", ", labels.Select(label => "'" + label + "'"));

    private static string RoleName(AudioRole role)
    {
        return role switch
        {
            AudioRole.Console => "default",
            AudioRole.Multimedia => "multimedia",
            _ => "communication",
        };
    }

    /// <summary>
    /// A notification of the operating system: read both directions again, and report the metrics whose value or options differ from
    /// what was reported last. Runs on a thread of the audio service or of the pool, never inside the callback of Windows.
    /// </summary>
    private void OnCatalogChanged()
    {
        Watcher[] targets;
        MetricChangeNotice notice;
        lock (_gate)
        {
            if (_watchers.Count == 0)
            {
                return;
            }

            AudioSnapshot output = Read(AudioFlow.Output);
            AudioSnapshot input = Read(AudioFlow.Input);
            var changed = new List<string>(2);
            bool options = false;
            Compare(_lastOutput, output, SessionMetricIds.AudioOutputDevice, changed, ref options);
            Compare(_lastInput, input, SessionMetricIds.AudioInputDevice, changed, ref options);
            _lastOutput = output;
            _lastInput = input;
            if (changed.Count == 0)
            {
                return;
            }

            notice = new MetricChangeNotice(changed, options);
            targets = [.. _watchers];
        }

        foreach (Watcher target in targets)
        {
#pragma warning disable CA1031 // A watcher that throws must not keep the others from hearing of the change.
            try
            {
                target.OnChange(notice);
            }
            catch (Exception exception)
            {
                _log.Warning("A watcher of the audio devices failed: " + exception.Message);
            }
#pragma warning restore CA1031
        }
    }

    private static void Compare(AudioSnapshot before, AudioSnapshot after, string metricId, List<string> changed, ref bool options)
    {
        bool optionsDiffer = !before.Labels.SequenceEqual(after.Labels, StringComparer.Ordinal);
        bool valueDiffers = !string.Equals(before.DefaultLabel, after.DefaultLabel, StringComparison.Ordinal);
        if (optionsDiffer || valueDiffers)
        {
            changed.Add(metricId);
        }

        options |= optionsDiffer;
    }

    private void Unwatch(Watcher watcher)
    {
        IDisposable? subscription = null;
        lock (_gate)
        {
            if (!_watchers.Remove(watcher) || _watchers.Count > 0)
            {
                return;
            }

            subscription = _subscription;
            _subscription = null;
        }

        subscription?.Dispose();
    }

    private sealed class Watcher : IDisposable
    {
        private readonly AudioDeviceService _owner;
        private int _disposed;

        public Watcher(AudioDeviceService owner, Action<MetricChangeNotice> onChange)
        {
            _owner = owner;
            OnChange = onChange;
        }

        public Action<MetricChangeNotice> OnChange { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unwatch(this);
            }
        }
    }
}
