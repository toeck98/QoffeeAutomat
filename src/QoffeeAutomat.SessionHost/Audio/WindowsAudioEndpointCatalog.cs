using System.Globalization;
using System.Runtime.InteropServices;
using static QoffeeAutomat.SessionHost.Audio.CoreAudioInterop;

namespace QoffeeAutomat.SessionHost.Audio;

/// <summary>
/// The <see cref="IAudioEndpointCatalog"/> of Windows (PLAN.md S11, T713), made of the documented Core Audio interfaces
/// (<c>IMMDeviceEnumerator</c>, <c>IMMDevice</c>, <c>IPropertyStore</c> and <c>IMMNotificationClient</c>). Every call opens what it
/// needs and releases it again, so nothing is held while nobody asks; the notification subscription is the only thing that lives
/// longer, and it exists only while the session client watches the devices. Without an audio service every read is empty, never an
/// exception. This class is verified on a Windows desktop; the logic above it is tested with a fake catalog.
/// </summary>
public sealed class WindowsAudioEndpointCatalog : IAudioEndpointCatalog
{
    /// <summary>Plugging in one device produces a burst of notifications; they are reported together after this time.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <summary>The property set of <c>DEVPKEY_DeviceInterface_FriendlyName</c>, the second part of the friendly name.</summary>
    private static readonly Guid InterfaceFriendlyNameSet = new("026E516E-B814-414B-83CD-856D6FEF4822");

    private readonly ISessionHostLog _log;

    /// <summary>Creates the catalog.</summary>
    /// <param name="log">The diagnostics log.</param>
    public WindowsAudioEndpointCatalog(ISessionHostLog? log = null)
    {
        _log = log ?? NullSessionHostLog.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioEndpoint> GetActiveEndpoints(AudioFlow flow)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        var endpoints = new List<AudioEndpoint>();
#pragma warning disable CA1031 // Whatever is wrong with the audio stack means "no devices"; this method does not throw.
        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(FlowOf(flow), DeviceStateActive, out collection) != 0
                || collection is null
                || collection.GetCount(out uint count) != 0)
            {
                return [];
            }

            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(index, out device) != 0 || device is null || device.GetId(out string id) != 0 || string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    endpoints.Add(new AudioEndpoint(id, ReadName(device)));
                }
                finally
                {
                    Release(device);
                }
            }

            return endpoints;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or InvalidOperationException)
        {
            _log.Warning("The audio devices could not be listed: " + exception.Message);
            return [];
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    public string? GetDefaultEndpointId(AudioFlow flow)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(FlowOf(flow), RoleOf(AudioRole.Console), out device) != 0
                || device is null
                || device.GetId(out string id) != 0)
            {
                return null;
            }

            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            Release(device);
            Release(enumerator);
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);

        try
        {
            return new Subscription(changed, _log);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            throw new AudioDeviceException("The audio service did not accept the registration for device notifications: " + exception.Message, exception);
        }
    }

    private static string ReadName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(StorageRead, out store) != 0 || store is null)
            {
                return string.Empty;
            }

            PropertyKey key = FriendlyName;
            if (store.GetValue(ref key, out PropVariant value) != 0)
            {
                return string.Empty;
            }

            try
            {
                return value.Type == VariantTypeString && value.Data != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.Data) ?? string.Empty
                    : string.Empty;
            }
            finally
            {
                _ = NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    /// <summary>
    /// The registration with Core Audio. Windows calls the methods of <see cref="IMMNotificationClient"/> on one of its own threads,
    /// where nothing may block and the audio API must not be called, so a call only starts (or restarts) a short timer; the
    /// callback of the subscriber runs when the timer fires, on a pool thread, after the burst of notifications is over.
    /// </summary>
    private sealed class Subscription : IMMNotificationClient, IDisposable
    {
        private readonly object _gate = new();
        private readonly Action _changed;
        private readonly ISessionHostLog _log;
        private readonly System.Threading.Timer _timer;
        private IMMDeviceEnumerator? _enumerator;
        private bool _pending;
        private bool _disposed;

        public Subscription(Action changed, ISessionHostLog log)
        {
            _changed = changed;
            _log = log;
            _timer = new System.Threading.Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            int result = enumerator.RegisterEndpointNotificationCallback(this);
            if (result != 0)
            {
                Release(enumerator);
                _timer.Dispose();
                throw new AudioDeviceException("Windows did not accept the registration for audio device notifications (error 0x" + result.ToString("X8", CultureInfo.InvariantCulture) + ").");
            }

            _enumerator = enumerator;
        }

        public int OnDeviceStateChanged(string deviceId, uint newState) => Schedule();

        public int OnDeviceAdded(string deviceId) => Schedule();

        public int OnDeviceRemoved(string deviceId) => Schedule();

        public int OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId) => Schedule();

        // Only the properties that make up the friendly name can rename an option. Windows reports many others (the effects of a
        // device, its format) and sometimes in a steady stream; they are ignored here, and a change that alters no label is dropped
        // by the service anyway, which compares what it reads.
        public int OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            return key.FormatId == FriendlyName.FormatId || key.FormatId == InterfaceFriendlyNameSet ? Schedule() : 0;
        }

        public void Dispose()
        {
            IMMDeviceEnumerator? enumerator;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                enumerator = _enumerator;
                _enumerator = null;
            }

#pragma warning disable CA1031 // Giving the registration back must not fail the session; Windows drops it with the process anyway.
            try
            {
                _ = enumerator?.UnregisterEndpointNotificationCallback(this);
            }
            catch (Exception exception)
            {
                _log.Warning("The audio device notifications could not be unregistered: " + exception.Message);
            }
#pragma warning restore CA1031

            Release(enumerator);
            _timer.Dispose();
        }

        /// <summary>Starts the timer on the first notification of a burst; later ones in the same burst do not restart it, so a steady stream cannot hold the report back.</summary>
        private int Schedule()
        {
            lock (_gate)
            {
                if (!_disposed && !_pending)
                {
                    _pending = true;
                    _timer.Change(Settle, Timeout.InfiniteTimeSpan);
                }
            }

            return 0;
        }

        private void Fire()
        {
            lock (_gate)
            {
                _pending = false;
                if (_disposed)
                {
                    return;
                }
            }

#pragma warning disable CA1031 // The subscriber must not be able to break the timer.
            try
            {
                _changed();
            }
            catch (Exception exception)
            {
                _log.Warning("A listener of the audio device notifications failed: " + exception.Message);
            }
#pragma warning restore CA1031
        }
    }
}
