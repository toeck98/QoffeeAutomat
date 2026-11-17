using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Platform.Windows.Metrics.SessionMetrics;
using QoffeeAutomat.Platform.Windows.Tests.Sessions;
using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.Platform.Windows.Tests.Metrics;

/// <summary>A probe that counts what is read and returns the values that the test sets.</summary>
internal sealed class CountingProbe : IUserMetricProbe
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

    public int TotalReads
    {
        get
        {
            lock (_gate)
            {
                return _reads.Values.Sum();
            }
        }
    }

    public void Set(string id, object? value)
    {
        lock (_gate)
        {
            _values[id] = value;
        }
    }

    public void Fail(string id)
    {
        lock (_gate)
        {
            _failing.Add(id);
        }
    }

    public int ReadsOf(string id)
    {
        lock (_gate)
        {
            return _reads.GetValueOrDefault(id);
        }
    }

    public object? Read(string metricId)
    {
        lock (_gate)
        {
            _reads[metricId] = _reads.GetValueOrDefault(metricId) + 1;
            if (_failing.Contains(metricId))
            {
                throw new InvalidOperationException("The probe is broken.");
            }

            return _values.GetValueOrDefault(metricId);
        }
    }
}

/// <summary>A registry of the current user that the test fills.</summary>
internal sealed class FakeUserRegistry : IUserRegistry
{
    private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a key and all keys above it, like the registry has them.</summary>
    public void AddKey(string path)
    {
        string current = string.Empty;
        foreach (string part in path.Split('\\'))
        {
            current = current.Length == 0 ? part : current + @"\" + part;
            if (!_keys.ContainsKey(current))
            {
                _keys[current] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void Set(string path, string name, object value)
    {
        AddKey(path);
        _keys[path][name] = value;
    }

    public string[]? GetSubKeyNames(string path)
    {
        if (!_keys.ContainsKey(path))
        {
            return null;
        }

        string prefix = path + @"\";
        return
        [
            .. _keys.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !key[prefix.Length..].Contains('\\', StringComparison.Ordinal))
                .Select(key => key[prefix.Length..])
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    public object? GetValue(string path, string name)
    {
        return _keys.TryGetValue(path, out Dictionary<string, object>? values) && values.TryGetValue(name, out object? value) ? value : null;
    }
}

/// <summary>
/// The session metrics (PLAN.md S06, S11, T305): the catalog, the SessionHost that samples only what the agent subscribed to,
/// the agent side with its session state, and the behaviour without a session. Most tests run on every operating system; the
/// real Windows probe runs on Windows only.
/// </summary>
[Trait("Category", "SessionMetrics")]
public sealed partial class SessionMetricsTests
{
    private const string Microphone = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private static readonly DateTimeOffset Boot = new(2026, 11, 16, 6, 0, 0, TimeSpan.Zero);

    [GeneratedRegex(@"^\d+x\d+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ResolutionPattern();

    private static object? ValueOf(SessionMetricsStateMessage message, string id)
    {
        JsonElement element = message.Values[id];
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            _ => null,
        };
    }

    private static long FileTime(DateTimeOffset moment) => moment.UtcDateTime.ToFileTimeUtc();

    // ------------------------------------------------------------------
    // The catalog
    // ------------------------------------------------------------------

    [Fact]
    public void The_catalog_describes_every_session_metric_once_with_the_privacy_sensitive_ones_off_by_default()
    {
        IReadOnlyList<MetricDescriptor> all = SessionMetricCatalog.All;

        Assert.Equal(all.Count, all.Select(descriptor => descriptor.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(22, all.Count);
        Assert.Equal(
            [
                SessionMetricIds.Ready,
                SessionMetricIds.Locked,
                SessionMetricIds.User,
                SessionMetricIds.IdleSeconds,
                SessionMetricIds.WindowTitle,
                SessionMetricIds.WindowProcess,
                SessionMetricIds.Volume,
                SessionMetricIds.Muted,
                SessionMetricIds.AudioOutputDevice,
                SessionMetricIds.AudioInputDevice,
                SessionMetricIds.MicrophoneInUse,
                SessionMetricIds.WebcamInUse,
                SessionMetricIds.DisplayCount,
                SessionMetricIds.PrimaryResolution,
                SessionMetricIds.MediaState,
                SessionMetricIds.MediaTitle,
                SessionMetricIds.MediaArtist,
                SessionMetricIds.MediaAlbum,
                SessionMetricIds.MediaApp,
                SessionMetricIds.MediaDuration,
                SessionMetricIds.MediaPosition,
                SessionMetricIds.MediaPositionUpdatedAt,
            ],
            all.Select(descriptor => descriptor.Id));

        // The window metrics reveal what the user does; the media metrics are optional and only used by the media player (T716).
        Assert.Equal(
            [
                SessionMetricIds.WindowTitle,
                SessionMetricIds.WindowProcess,
                SessionMetricIds.MediaState,
                SessionMetricIds.MediaTitle,
                SessionMetricIds.MediaArtist,
                SessionMetricIds.MediaAlbum,
                SessionMetricIds.MediaApp,
                SessionMetricIds.MediaDuration,
                SessionMetricIds.MediaPosition,
                SessionMetricIds.MediaPositionUpdatedAt,
            ],
            all.Where(descriptor => descriptor.EnabledDefault == false).Select(descriptor => descriptor.Id));
        Assert.All(all.Where(descriptor => descriptor.EnabledDefault != false), descriptor => Assert.True(descriptor.EnabledDefault));
        Assert.All(all, descriptor => Assert.False(string.IsNullOrWhiteSpace(descriptor.TranslationKey)));
    }

    [Fact]
    public void The_metrics_have_the_kinds_units_and_contexts_of_the_plan()
    {
        MetricDescriptor Find(string id) => SessionMetricCatalog.All.Single(descriptor => descriptor.Id == id);

        Assert.Equal(("binary_sensor", "bool", "system"), (Find("session.ready").Kind, Find("session.ready").ValueType, Find("session.ready").Context));
        Assert.Equal(("binary_sensor", "bool"), (Find("session.locked").Kind, Find("session.locked").ValueType));
        Assert.Equal(("sensor", "string"), (Find("session.user").Kind, Find("session.user").ValueType));
        Assert.Equal(("s", "duration", "measurement"), (Find("session.idle_seconds").Unit, Find("session.idle_seconds").DeviceClass, Find("session.idle_seconds").StateClass));
        Assert.Equal("%", Find("audio.volume").Unit);
        Assert.Equal("binary_sensor", Find("audio.muted").Kind);
        Assert.Equal("binary_sensor", Find("privacy.microphone_in_use").Kind);
        Assert.Equal("binary_sensor", Find("privacy.webcam_in_use").Kind);
        Assert.Equal(("number", "diagnostic"), (Find("display.count").ValueType, Find("display.count").EntityCategory));
        Assert.Equal(("string", "diagnostic"), (Find("display.primary_resolution").ValueType, Find("display.primary_resolution").EntityCategory));

        // Only what the SessionHost samples is context user and listed as its metrics; ready is the agent's own.
        Assert.All(SessionMetricCatalog.SessionHostSide, descriptor => Assert.Equal("user", descriptor.Context));
        Assert.All(SessionMetricCatalog.SessionHostSide, descriptor => Assert.True(descriptor.IntervalSeconds > 0));
        Assert.False(SessionMetricCatalog.IsSessionHostMetric(SessionMetricIds.Ready));
        Assert.True(SessionMetricCatalog.IsSessionHostMetric(SessionMetricIds.Volume));
        Assert.Equal(5, SessionMetricCatalog.IntervalSecondsOf("unknown.metric"));
    }

    // ------------------------------------------------------------------
    // The sampler of the SessionHost
    // ------------------------------------------------------------------

    [Fact]
    public void Nothing_is_read_until_the_agent_subscribes_and_an_empty_subscription_stops_the_sampling()
    {
        var probe = new CountingProbe();
        var time = new ManualTime();
        using var sampler = new UserMetricSampler(probe, time);

        Assert.Null(sampler.SampleDue());
        Assert.Equal(0, probe.TotalReads);

        SessionMetricsStateMessage? first = sampler.Subscribe([SessionMetricIds.WindowTitle]);
        Assert.NotNull(first);
        Assert.Equal(1, probe.ReadsOf(SessionMetricIds.WindowTitle));
        Assert.True(sampler.IsSubscribed);

        Assert.Null(sampler.Subscribe([]));
        time.Now += TimeSpan.FromMinutes(5);
        Assert.False(sampler.IsSubscribed);
        Assert.Null(sampler.SampleDue());
        Assert.Equal(1, probe.TotalReads);
    }

    [Fact]
    public void A_subscription_is_answered_at_once_with_the_values_and_unknown_ids_are_unavailable()
    {
        var probe = new CountingProbe();
        probe.Set(SessionMetricIds.IdleSeconds, 12.0);
        probe.Set(SessionMetricIds.Muted, true);
        probe.Set(SessionMetricIds.PrimaryResolution, "2560x1440");
        var time = new ManualTime();
        using var sampler = new UserMetricSampler(probe, time);

        SessionMetricsStateMessage message = sampler.Subscribe(
            [SessionMetricIds.IdleSeconds, SessionMetricIds.Muted, SessionMetricIds.PrimaryResolution, SessionMetricIds.Volume, "session.idle_time", SessionMetricIds.Ready])!;

        Assert.Equal(time.Now.ToUnixTimeMilliseconds(), message.Ts);
        Assert.Equal(12.0, ValueOf(message, SessionMetricIds.IdleSeconds));
        Assert.Equal(true, ValueOf(message, SessionMetricIds.Muted));
        Assert.Equal("2560x1440", ValueOf(message, SessionMetricIds.PrimaryResolution));
        Assert.Null(ValueOf(message, SessionMetricIds.Volume));
        Assert.Null(ValueOf(message, "session.idle_time"));
        Assert.Null(ValueOf(message, SessionMetricIds.Ready));
        Assert.Equal(6, message.Values.Count);

        // Only the metrics of the session are ever read; the others were answered as unavailable.
        Assert.Equal(0, probe.ReadsOf("session.idle_time"));
        Assert.Equal(0, probe.ReadsOf(SessionMetricIds.Ready));
        Assert.Equal(
            ["audio.muted", "audio.volume", "display.primary_resolution", "session.idle_seconds"],
            sampler.Subscribed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Each_metric_is_sampled_at_the_interval_of_its_descriptor()
    {
        var probe = new CountingProbe();
        var time = new ManualTime();
        using var sampler = new UserMetricSampler(probe, time);
        sampler.Subscribe([SessionMetricIds.IdleSeconds, SessionMetricIds.WindowTitle, SessionMetricIds.DisplayCount]);

        // Window title: every 2 s, idle time: every 5 s, display count: every 30 s.
        time.Now += TimeSpan.FromSeconds(1);
        Assert.Null(sampler.SampleDue());

        time.Now += TimeSpan.FromSeconds(1);
        SessionMetricsStateMessage afterTwo = sampler.SampleDue()!;
        Assert.Equal([SessionMetricIds.WindowTitle], afterTwo.Values.Keys);

        time.Now += TimeSpan.FromSeconds(3);
        SessionMetricsStateMessage afterFive = sampler.SampleDue()!;
        Assert.Equal([SessionMetricIds.IdleSeconds, SessionMetricIds.WindowTitle], afterFive.Values.Keys.Order(StringComparer.Ordinal));

        time.Now += TimeSpan.FromSeconds(30);
        Assert.Equal(3, sampler.SampleDue()!.Values.Count);
        Assert.Equal(2, probe.ReadsOf(SessionMetricIds.DisplayCount));
    }

    [Fact]
    public void A_probe_that_fails_makes_only_its_metric_unavailable_and_numbers_that_are_not_finite_are_unavailable()
    {
        var probe = new CountingProbe();
        probe.Set(SessionMetricIds.Volume, double.NaN);
        probe.Set(SessionMetricIds.IdleSeconds, 3.0);
        probe.Fail(SessionMetricIds.WindowTitle);
        using var sampler = new UserMetricSampler(probe, new ManualTime());

        SessionMetricsStateMessage message = sampler.Subscribe([SessionMetricIds.WindowTitle, SessionMetricIds.Volume, SessionMetricIds.IdleSeconds])!;

        Assert.Null(ValueOf(message, SessionMetricIds.WindowTitle));
        Assert.Null(ValueOf(message, SessionMetricIds.Volume));
        Assert.Equal(3.0, ValueOf(message, SessionMetricIds.IdleSeconds));
    }

    [Fact]
    public async Task The_background_loop_publishes_while_subscribed_and_stops_with_the_token()
    {
        var probe = new CountingProbe();
        probe.Set(SessionMetricIds.WindowProcess, "notepad");
        var time = new ManualTime();
        using var sampler = new UserMetricSampler(probe, time);
        var published = new List<SessionMetricsStateMessage>();
        using var cancellation = new CancellationTokenSource();
        Task loop = sampler.RunAsync(
            message =>
            {
                lock (published)
                {
                    published.Add(message);
                }

                return Task.CompletedTask;
            },
            cancellation.Token);

        // Nothing is subscribed: the loop sleeps and publishes nothing.
        await Task.Delay(700);
        lock (published)
        {
            Assert.Empty(published);
        }

        sampler.Subscribe([SessionMetricIds.WindowProcess]);
        time.Now += TimeSpan.FromSeconds(3);
        await TestWait.UntilAsync(() => { lock (published) { return published.Count >= 1; } }, "the loop to publish");

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        lock (published)
        {
            Assert.Equal("notepad", ValueOf(published[0], SessionMetricIds.WindowProcess));
        }
    }

    // ------------------------------------------------------------------
    // Microphone and webcam
    // ------------------------------------------------------------------

    [Fact]
    public void The_microphone_is_in_use_while_an_application_started_and_did_not_stop()
    {
        var registry = new FakeUserRegistry();
        registry.Set(Microphone + @"\Microsoft.WindowsCamera_8wekyb3d8bbwe", "LastUsedTimeStart", FileTime(Boot.AddHours(1)));
        registry.Set(Microphone + @"\Microsoft.WindowsCamera_8wekyb3d8bbwe", "LastUsedTimeStop", FileTime(Boot.AddHours(2)));
        var access = new CapabilityAccess(registry, () => Boot);

        Assert.False(access.IsInUse(CapabilityAccess.Microphone));

        registry.Set(Microphone + @"\Microsoft.WindowsCamera_8wekyb3d8bbwe", "LastUsedTimeStart", FileTime(Boot.AddHours(3)));
        Assert.True(access.IsInUse(CapabilityAccess.Microphone));

        registry.Set(Microphone + @"\Microsoft.WindowsCamera_8wekyb3d8bbwe", "LastUsedTimeStop", FileTime(Boot.AddHours(4)));
        Assert.False(access.IsInUse(CapabilityAccess.Microphone));
    }

    [Fact]
    public void Desktop_applications_are_listed_below_non_packaged_and_a_stop_time_of_zero_means_in_use()
    {
        var registry = new FakeUserRegistry();
        registry.AddKey(Microphone);
        registry.Set(Microphone + @"\NonPackaged\C:#Program Files#Zoom#Zoom.exe", "LastUsedTimeStart", FileTime(Boot.AddHours(1)));
        registry.Set(Microphone + @"\NonPackaged\C:#Program Files#Zoom#Zoom.exe", "LastUsedTimeStop", 0L);
        var access = new CapabilityAccess(registry, () => Boot);

        Assert.True(access.IsInUse(CapabilityAccess.Microphone));
    }

    [Fact]
    public void A_start_without_a_stop_from_before_the_last_boot_is_a_leftover_and_values_may_be_binary()
    {
        var registry = new FakeUserRegistry();
        registry.Set(Microphone + @"\Crashed.App", "LastUsedTimeStart", FileTime(Boot.AddDays(-3)));
        registry.Set(Microphone + @"\Crashed.App", "LastUsedTimeStop", 0L);
        var access = new CapabilityAccess(registry, () => Boot);
        Assert.False(access.IsInUse(CapabilityAccess.Microphone));

        // REG_BINARY values of eight bytes are read as the same integer.
        registry.Set(Microphone + @"\Binary.App", "LastUsedTimeStart", BitConverter.GetBytes(FileTime(Boot.AddHours(1))));
        registry.Set(Microphone + @"\Binary.App", "LastUsedTimeStop", BitConverter.GetBytes(0L));
        Assert.True(access.IsInUse(CapabilityAccess.Microphone));
    }

    [Fact]
    public void Without_the_consent_store_the_state_is_unknown_and_the_webcam_is_its_own_capability()
    {
        var registry = new FakeUserRegistry();
        var access = new CapabilityAccess(registry, () => Boot);
        Assert.Null(access.IsInUse(CapabilityAccess.Microphone));
        Assert.Null(access.IsInUse(CapabilityAccess.Webcam));

        string webcam = Microphone.Replace("microphone", "webcam", StringComparison.Ordinal);
        registry.Set(webcam + @"\Some.App", "LastUsedTimeStart", FileTime(Boot.AddHours(1)));
        registry.Set(webcam + @"\Some.App", "LastUsedTimeStop", 0L);

        Assert.True(access.IsInUse(CapabilityAccess.Webcam));
        Assert.Null(access.IsInUse(CapabilityAccess.Microphone));
    }

    // ------------------------------------------------------------------
    // Agent and SessionHost together
    // ------------------------------------------------------------------

    [Fact]
    public async Task Session_ready_turns_on_when_the_session_host_is_connected_and_off_when_it_ends()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();

        IReadOnlyDictionary<string, object?> before = await harness.Bridge.SampleUserMetricsAsync(
            [SessionMetricIds.Ready, SessionMetricIds.User, SessionMetricIds.Locked],
            CancellationToken.None);
        Assert.Equal(false, before[SessionMetricIds.Ready]);

        using RunningHost host = harness.StartHost();
        await harness.NextSessionReadyAsync();
        IReadOnlyDictionary<string, object?> ready = await harness.Bridge.SampleUserMetricsAsync(
            [SessionMetricIds.Ready, SessionMetricIds.User, SessionMetricIds.Locked],
            CancellationToken.None);

        Assert.Equal(true, ready[SessionMetricIds.Ready]);
        Assert.Equal("alice", ready[SessionMetricIds.User]);
        Assert.Equal(false, ready[SessionMetricIds.Locked]);

        await host.StopAsync();
        await TestWait.UntilAsync(() => !harness.Bridge.State.IsReady, "the session to stop being ready");
        IReadOnlyDictionary<string, object?> ended = await harness.Bridge.SampleUserMetricsAsync([SessionMetricIds.Ready], CancellationToken.None);
        Assert.Equal(false, ended[SessionMetricIds.Ready]);
    }

    [Fact]
    public async Task Locking_and_unlocking_the_computer_updates_the_metric_at_once()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost();
        await harness.NextSessionReadyAsync();

        harness.Monitor.Handle(System.ServiceProcess.SessionChangeReason.SessionLock, SessionHarness.SessionId);
        IReadOnlyDictionary<string, object?> locked = await harness.Bridge.SampleUserMetricsAsync([SessionMetricIds.Locked], CancellationToken.None);
        harness.Monitor.Handle(System.ServiceProcess.SessionChangeReason.SessionUnlock, SessionHarness.SessionId);
        IReadOnlyDictionary<string, object?> unlocked = await harness.Bridge.SampleUserMetricsAsync([SessionMetricIds.Locked], CancellationToken.None);

        Assert.Equal(true, locked[SessionMetricIds.Locked]);
        Assert.Equal(false, unlocked[SessionMetricIds.Locked]);
    }

    [Fact]
    public async Task With_no_user_logged_in_every_user_metric_is_unavailable_and_nothing_is_logged_as_an_error()
    {
        using var harness = new SessionHarness();
        harness.Monitor.Handle(System.ServiceProcess.SessionChangeReason.SessionLogoff, SessionHarness.SessionId);
        harness.Lookup.Console = null;
        harness.Lookup.User = null;
        await harness.StartAsync();

        IReadOnlyDictionary<string, object?> values = await harness.Bridge.SampleUserMetricsAsync(
            [.. SessionMetricCatalog.All.Select(descriptor => descriptor.Id)],
            CancellationToken.None);

        Assert.Equal(SessionMetricCatalog.All.Count, values.Count);
        Assert.Equal(false, values[SessionMetricIds.Ready]);
        Assert.All(
            values.Where(pair => pair.Key != SessionMetricIds.Ready),
            pair => Assert.True(pair.Value is null, pair.Key + " should be unavailable but is " + pair.Value));
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level >= LogLevel.Warning && entry.Message.Contains("metric", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_session_that_is_logged_in_without_a_session_host_has_no_user_metrics_and_the_agent_does_not_complain()
    {
        using var harness = new SessionHarness();
        await harness.StartAsync();

        IReadOnlyDictionary<string, object?> values = await harness.Bridge.SampleUserMetricsAsync(
            [SessionMetricIds.IdleSeconds, SessionMetricIds.Volume, SessionMetricIds.User, SessionMetricIds.Locked],
            CancellationToken.None);

        Assert.Null(values[SessionMetricIds.IdleSeconds]);
        Assert.Null(values[SessionMetricIds.Volume]);
        Assert.Equal("alice", values[SessionMetricIds.User]);
        Assert.Equal(false, values[SessionMetricIds.Locked]);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task User_metrics_travel_over_the_session_pipe_and_are_sampled_only_while_the_agent_is_subscribed()
    {
        var probe = new CountingProbe();
        probe.Set(SessionMetricIds.IdleSeconds, 12.0);
        probe.Set(SessionMetricIds.Muted, true);
        probe.Set(SessionMetricIds.PrimaryResolution, "1920x1080");
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost(metrics: probe);
        await harness.NextSessionReadyAsync();

        // Connected, but nobody asked: nothing is read in the session.
        await Task.Delay(600);
        Assert.Equal(0, probe.TotalReads);

        IReadOnlyDictionary<string, object?> values = await harness.Bridge.SampleUserMetricsAsync(
            [SessionMetricIds.IdleSeconds, SessionMetricIds.Muted, SessionMetricIds.PrimaryResolution, SessionMetricIds.Volume],
            CancellationToken.None);

        Assert.Equal(12.0, values[SessionMetricIds.IdleSeconds]);
        Assert.Equal(true, values[SessionMetricIds.Muted]);
        Assert.Equal("1920x1080", values[SessionMetricIds.PrimaryResolution]);
        Assert.Null(values[SessionMetricIds.Volume]);
        Assert.Equal(0, probe.ReadsOf(SessionMetricIds.WindowTitle));
        Assert.Equal(0, probe.ReadsOf(SessionMetricIds.WebcamInUse));

        // A changed value arrives with a later sample of the engine.
        probe.Set(SessionMetricIds.IdleSeconds, 99.0);
        await TestWait.UntilAsync(
            () => harness.Bridge.SampleUserMetricsAsync([SessionMetricIds.IdleSeconds], CancellationToken.None).AsTask().GetAwaiter().GetResult()[SessionMetricIds.IdleSeconds] is 99.0,
            "the new idle time",
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_metric_that_the_engine_stops_asking_for_leaves_the_subscription_and_the_session_stops_sampling_it()
    {
        var probe = new CountingProbe();
        probe.Set(SessionMetricIds.WindowTitle, "Notepad");
        var clock = new ManualTime();
        using var relay = new UserMetricsRelay(clock, pruneInterval: Timeout.InfiniteTimeSpan);
        using var harness = new SessionHarness(metricsRelay: relay);
        await harness.StartAsync();
        using RunningHost host = harness.StartHost(metrics: probe);
        await harness.NextSessionReadyAsync();

        IReadOnlyDictionary<string, object?> values = await harness.Bridge.SampleUserMetricsAsync([SessionMetricIds.WindowTitle], CancellationToken.None);
        Assert.Equal("Notepad", values[SessionMetricIds.WindowTitle]);
        Assert.Equal([SessionMetricIds.WindowTitle], relay.Subscribed);

        // The window title has an interval of 2 s: while it is subscribed the session keeps reading it.
        await TestWait.UntilAsync(() => probe.ReadsOf(SessionMetricIds.WindowTitle) >= 2, "a second sample in the session", TimeSpan.FromSeconds(30));

        // Nobody asked for it for a minute: the next check drops it and the session is told to stop.
        clock.Now += TimeSpan.FromMinutes(1);
        relay.Prune();
        Assert.Empty(relay.Subscribed);
        await Task.Delay(500);
        int reads = probe.ReadsOf(SessionMetricIds.WindowTitle);
        await Task.Delay(2800);

        Assert.Equal(reads, probe.ReadsOf(SessionMetricIds.WindowTitle));
    }

    [Fact]
    public async Task A_new_session_host_is_subscribed_to_the_metrics_that_are_still_wanted()
    {
        var firstProbe = new CountingProbe();
        firstProbe.Set(SessionMetricIds.Volume, 20.0);
        var secondProbe = new CountingProbe();
        secondProbe.Set(SessionMetricIds.Volume, 55.0);
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost first = harness.StartHost(metrics: firstProbe);
        await harness.NextSessionReadyAsync();
        Assert.Equal(20.0, (await harness.Bridge.SampleUserMetricsAsync([SessionMetricIds.Volume], CancellationToken.None))[SessionMetricIds.Volume]);

        // The session host restarts (a crash, a new version): the agent subscribes the new one without being asked again.
        await first.StopAsync();
        await TestWait.UntilAsync(() => !harness.Bridge.State.IsReady, "the first session host to be gone");
        using RunningHost second = harness.StartHost(metrics: secondProbe);
        await harness.NextSessionReadyAsync();

        await TestWait.UntilAsync(() => secondProbe.ReadsOf(SessionMetricIds.Volume) >= 1, "the new session host to be subscribed");
        Assert.Equal(55.0, (await harness.Bridge.SampleUserMetricsAsync([SessionMetricIds.Volume], CancellationToken.None))[SessionMetricIds.Volume]);
    }

    // ------------------------------------------------------------------
    // The real Windows
    // ------------------------------------------------------------------

    [WindowsFact]
    public void The_real_probe_returns_plausible_values_or_unavailable_for_every_metric()
    {
        var probe = new WindowsUserMetricProbe();

        foreach (MetricDescriptor descriptor in SessionMetricCatalog.SessionHostSide)
        {
            object? value = probe.Read(descriptor.Id);
            if (value is null)
            {
                // No audio device, no foreground window on a build agent without a desktop: a clean unavailable state.
                continue;
            }

            switch (descriptor.Id)
            {
                case SessionMetricIds.IdleSeconds:
                    Assert.InRange(Assert.IsType<double>(value), 0, 60 * 60 * 24 * 365);
                    break;
                case SessionMetricIds.Volume:
                    Assert.InRange(Assert.IsType<double>(value), 0, 100);
                    break;
                case SessionMetricIds.DisplayCount:
                    Assert.InRange(Assert.IsType<double>(value), 1, 64);
                    break;
                case SessionMetricIds.PrimaryResolution:
                    Assert.Matches(ResolutionPattern(), Assert.IsType<string>(value));
                    break;
                case SessionMetricIds.Muted:
                case SessionMetricIds.MicrophoneInUse:
                case SessionMetricIds.WebcamInUse:
                    Assert.IsType<bool>(value);
                    break;
                default:
                    Assert.IsType<string>(value);
                    break;
            }
        }

        Assert.Null(probe.Read("not.a.metric"));
        Assert.Null(probe.Read(SessionMetricIds.Ready));
    }

    [WindowsFact]
    public void The_real_audio_endpoint_is_read_without_throwing()
    {
        AudioEndpointState? state = CoreAudioEndpoint.TryRead();

        if (state is { } audio)
        {
            Assert.InRange(audio.VolumePercent, 0, 100);
        }
    }
}
