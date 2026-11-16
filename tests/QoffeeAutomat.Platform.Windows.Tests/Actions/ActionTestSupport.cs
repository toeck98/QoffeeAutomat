using System.Text.Json;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.SessionHost.Actions;
using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// Builders for the tests of the built-in actions. Nothing in this folder touches the computer: the power, desktop,
/// audio and process calls all go to the fakes below, and a test that needs a failure makes the fake throw it.
/// </summary>
internal static class ActionTestData
{
    public static IReadOnlyDictionary<string, object> Vars(params (string Name, object Value)[] values)
    {
        return values.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);
    }

    /// <summary>Asserts the recorded calls of a fake, in order; the failure message lists what was recorded.</summary>
    public static void AssertCalls(IReadOnlyList<string> actual, params string[] expected)
    {
        Assert.True(
            expected.SequenceEqual(actual),
            "Expected the calls [" + string.Join(", ", expected) + "] but got [" + string.Join(", ", actual) + "].");
    }

    /// <summary>Asserts the process ids that a fake ended, in order.</summary>
    public static void AssertIds(IReadOnlyList<int> actual, params int[] expected)
    {
        Assert.True(
            expected.SequenceEqual(actual),
            "Expected the ids [" + string.Join(", ", expected) + "] but got [" + string.Join(", ", actual) + "].");
    }

    public static JsonElement Json(string raw)
    {
        using JsonDocument document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    public static IReadOnlyDictionary<string, JsonElement> Supplied(params (string Name, string Json)[] values)
    {
        return values.ToDictionary(entry => entry.Name, entry => Json(entry.Json), StringComparer.Ordinal);
    }
}

/// <summary>An <see cref="IPowerNativeApi"/> that records the requests and never touches the power state of the computer.</summary>
internal sealed class FakePowerNativeApi : IPowerNativeApi
{
    public bool CanSleep { get; set; } = true;

    public bool CanHibernate { get; set; } = true;

    /// <summary>Thrown by every call after it was recorded, to test the failure path.</summary>
    public Exception? Failure { get; set; }

    public List<string> Calls { get; } = [];

    public void Shutdown(bool force) => Record(force ? "shutdown:force" : "shutdown");

    public void Restart(bool force) => Record(force ? "restart:force" : "restart");

    public void Sleep() => Record("sleep");

    public void Hibernate() => Record("hibernate");

    private void Record(string call)
    {
        Calls.Add(call);
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

/// <summary>An <see cref="IDesktopNativeApi"/> that records the requests and never locks, logs off or starts anything.</summary>
internal sealed class FakeDesktopNativeApi : IDesktopNativeApi
{
    public Exception? Failure { get; set; }

    public List<string> Calls { get; } = [];

    public List<(string Path, string? Arguments)> Launches { get; } = [];

    public List<Uri> OpenedUrls { get; } = [];

    public List<string> MediaKeys { get; } = [];

    public void LockWorkstation() => Record("lock");

    public void LogOff(bool force) => Record(force ? "logoff:force" : "logoff");

    public void TurnDisplayOff() => Record("display_off");

    public void SendMediaKey(string key)
    {
        MediaKeys.Add(key);
        Record("media_key");
    }

    public void Launch(string path, string? arguments)
    {
        Launches.Add((path, arguments));
        Record("launch");
    }

    public void OpenUrl(Uri url)
    {
        OpenedUrls.Add(url);
        Record("open_url");
    }

    private void Record(string call)
    {
        Calls.Add(call);
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

/// <summary>An <see cref="IAudioEndpoint"/> in memory.</summary>
internal sealed class FakeAudioEndpoint : IAudioEndpoint
{
    public bool HasDevice { get; set; } = true;

    public double Volume { get; private set; } = 30;

    public bool Muted { get; private set; }

    public AudioEndpointState? Read() => HasDevice ? new AudioEndpointState(Volume, Muted) : null;

    public bool TrySetVolume(double percent)
    {
        if (!HasDevice)
        {
            return false;
        }

        Volume = percent;
        return true;
    }

    public bool TrySetMute(bool muted)
    {
        if (!HasDevice)
        {
            return false;
        }

        Muted = muted;
        return true;
    }
}

/// <summary>An <see cref="IProcessControl"/> over a made-up process table: nothing is ever ended for real.</summary>
internal sealed class FakeProcessControl : IProcessControl
{
    private readonly Dictionary<string, int[]> _table = new(StringComparer.OrdinalIgnoreCase);

    public int CurrentProcessId => 4242;

    /// <summary>Processes by id that cannot be ended, with the reason.</summary>
    public Dictionary<int, string> Stubborn { get; } = [];

    public List<int> Ended { get; } = [];

    public List<string> Lookups { get; } = [];

    public void Add(string name, params int[] ids) => _table[name] = ids;

    public IReadOnlyList<int> FindByName(string name)
    {
        Lookups.Add(name);
        return _table.TryGetValue(name, out int[]? ids) ? ids : [];
    }

    public Task<string?> KillTreeAsync(int processId, CancellationToken cancellationToken)
    {
        if (Stubborn.TryGetValue(processId, out string? reason))
        {
            return Task.FromResult<string?>(reason);
        }

        Ended.Add(processId);
        return Task.FromResult<string?>(null);
    }
}
