using System.Runtime.InteropServices;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.Platform.Windows.Tests.Actions;
using QoffeeAutomat.Platform.Windows.Tests.Metrics;
using QoffeeAutomat.Platform.Windows.Tests.Sessions;
using QoffeeAutomat.SessionHost;
using QoffeeAutomat.SessionHost.Actions;
using QoffeeAutomat.SessionHost.Audio;
using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.Platform.Windows.Tests.Audio;

/// <summary>
/// The Core Audio part of the audio devices as the test controls it: the active devices, the default device and the
/// notifications. It is the mocked native layer that the logic of <see cref="AudioDeviceService"/> runs against.
/// </summary>
internal sealed class FakeAudioCatalog : IAudioEndpointCatalog
{
    private readonly object _gate = new();
    private Action? _callback;

    public List<AudioEndpoint> Outputs { get; } = [];

    public List<AudioEndpoint> Inputs { get; } = [];

    public string? DefaultOutputId { get; set; }

    public string? DefaultInputId { get; set; }

    /// <summary>When set, listing the devices throws it, like a broken audio stack.</summary>
    public Exception? ListFailure { get; set; }

    /// <summary>When set, subscribing throws it.</summary>
    public Exception? SubscribeFailure { get; set; }

    public int Subscriptions { get; private set; }

    public int Unsubscriptions { get; private set; }

    public bool IsSubscribed
    {
        get
        {
            lock (_gate)
            {
                return _callback is not null;
            }
        }
    }

    public IReadOnlyList<AudioEndpoint> GetActiveEndpoints(AudioFlow flow)
    {
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        lock (_gate)
        {
            return [.. (flow == AudioFlow.Output ? Outputs : Inputs)];
        }
    }

    public string? GetDefaultEndpointId(AudioFlow flow) => flow == AudioFlow.Output ? DefaultOutputId : DefaultInputId;

    public IDisposable Subscribe(Action changed)
    {
        if (SubscribeFailure is not null)
        {
            throw SubscribeFailure;
        }

        lock (_gate)
        {
            _callback = changed;
            Subscriptions++;
        }

        return new Registration(this);
    }

    /// <summary>Plays the notification that Windows sends after a device changed.</summary>
    public void Raise()
    {
        Action? callback;
        lock (_gate)
        {
            callback = _callback;
        }

        callback?.Invoke();
    }

    private sealed class Registration : IDisposable
    {
        private readonly FakeAudioCatalog _owner;

        public Registration(FakeAudioCatalog owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                _owner._callback = null;
                _owner.Unsubscriptions++;
            }
        }
    }
}

/// <summary>The undocumented part (<c>IPolicyConfig</c>) as the test controls it: it records what was asked and can fail.</summary>
internal sealed class FakeEndpointSwitch : IDefaultEndpointSwitch
{
    public List<(string Id, AudioRole Role)> Calls { get; } = [];

    /// <summary>When set, the call with this number (counting from 1) throws <see cref="Failure"/>.</summary>
    public int? FailOnCall { get; set; }

    public Exception Failure { get; set; } = new AudioDeviceException("Windows refused the change (error 0x80070005).");

    /// <summary>Called after a successful call, to let the fake catalog follow like Windows does.</summary>
    public Action<string, AudioRole>? OnSet { get; set; }

    public void SetDefault(string endpointId, AudioRole role)
    {
        Calls.Add((endpointId, role));
        if (FailOnCall == Calls.Count)
        {
            throw Failure;
        }

        OnSet?.Invoke(endpointId, role);
    }
}

/// <summary>A log that remembers its warnings.</summary>
internal sealed class RecordingLog : ISessionHostLog
{
    public List<string> Warnings { get; } = [];

    public void Info(string message)
    {
    }

    public void Warning(string message) => Warnings.Add(message);
}

/// <summary>
/// Audio device switching in the session helper (PLAN.md S06, S11, T713), against a fake Core Audio and a fake
/// <c>IPolicyConfig</c>: which devices are listed, how the default is reported, when the options change, how the variable
/// <c>Device</c> is matched, and what is (and is not) changed. The COM classes themselves are only exercised by the
/// Windows-only tests at the end.
/// </summary>
[Trait("Category", "AudioDevices")]
public sealed class AudioDeviceTests
{
    private const string SpeakersId = "{0.0.0.00000000}.{speakers}";
    private const string HeadphonesId = "{0.0.0.00000000}.{headphones}";
    private const string TvId = "{0.0.0.00000000}.{tv}";
    private const string MicId = "{0.0.1.00000000}.{mic}";
    private const string WebcamId = "{0.0.1.00000000}.{webcam}";

    private readonly FakeAudioCatalog _catalog = new();
    private readonly FakeEndpointSwitch _switch = new();
    private readonly RecordingLog _log = new();

    public AudioDeviceTests()
    {
        _catalog.Outputs.AddRange(
        [
            new AudioEndpoint(SpeakersId, "Speakers (Realtek Audio)"),
            new AudioEndpoint(HeadphonesId, "Headphones (USB Headset)"),
            new AudioEndpoint(TvId, "LG TV (NVIDIA High Definition Audio)"),
        ]);
        _catalog.Inputs.AddRange(
        [
            new AudioEndpoint(MicId, "Microphone (USB Headset)"),
            new AudioEndpoint(WebcamId, "Webcam Microphone"),
        ]);
        _catalog.DefaultOutputId = SpeakersId;
        _catalog.DefaultInputId = MicId;
    }

    private AudioDeviceService NewService() => new(_catalog, _switch, _log);

    // ------------------------------------------------------------------
    // Enumeration
    // ------------------------------------------------------------------

    [Fact]
    public void The_active_devices_are_listed_by_name_and_the_default_is_marked()
    {
        AudioSnapshot output = NewService().Read(AudioFlow.Output);

        Assert.Equal(
            ["Headphones (USB Headset)", "LG TV (NVIDIA High Definition Audio)", "Speakers (Realtek Audio)"],
            output.Labels);
        Assert.Equal("Speakers (Realtek Audio)", output.DefaultLabel);
        Assert.Equal(SpeakersId, output.DefaultId);
    }

    [Fact]
    public void Playback_and_recording_devices_are_kept_apart()
    {
        AudioDeviceService service = NewService();

        Assert.Equal(["Microphone (USB Headset)", "Webcam Microphone"], service.Read(AudioFlow.Input).Labels);
        Assert.Equal("Microphone (USB Headset)", service.CurrentLabel(AudioFlow.Input));
        Assert.DoesNotContain("Webcam Microphone", service.Read(AudioFlow.Output).Labels);
    }

    [Fact]
    public void The_order_does_not_depend_on_the_order_in_which_windows_lists_the_devices()
    {
        List<AudioEndpoint> reversed = [.. _catalog.Outputs.AsEnumerable().Reverse()];
        string[] first = [.. NewService().Read(AudioFlow.Output).Labels];

        _catalog.Outputs.Clear();
        _catalog.Outputs.AddRange(reversed);
        string[] second = [.. NewService().Read(AudioFlow.Output).Labels];

        Assert.Equal(first, second);
    }

    [Fact]
    public void Two_devices_with_the_same_name_get_numbered_labels_that_follow_the_endpoint_ids()
    {
        _catalog.Outputs.Clear();
        _catalog.Outputs.AddRange(
        [
            new AudioEndpoint("{b}", "USB Headset"),
            new AudioEndpoint("{a}", "USB Headset"),
            new AudioEndpoint("{c}", "USB Headset"),
        ]);
        _catalog.DefaultOutputId = "{b}";

        AudioSnapshot output = NewService().Read(AudioFlow.Output);

        Assert.Equal(["USB Headset", "USB Headset #2", "USB Headset #3"], output.Labels);
        Assert.Equal("{a}", output.Devices.Single(device => device.Label == "USB Headset").Id);
        Assert.Equal("{b}", output.Devices.Single(device => device.Label == "USB Headset #2").Id);
        Assert.Equal("USB Headset #2", output.DefaultLabel);
    }

    [Fact]
    public void A_generated_label_never_collides_with_a_real_name()
    {
        IReadOnlyList<AudioDevice> devices = AudioDeviceMatcher.Label(
        [
            new AudioEndpoint("{1}", "Headset"),
            new AudioEndpoint("{2}", "Headset"),
            new AudioEndpoint("{3}", "Headset #2"),
        ]);

        Assert.Equal(3, devices.Select(device => device.Label).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_device_without_a_name_is_still_listed()
    {
        _catalog.Outputs.Clear();
        _catalog.Outputs.Add(new AudioEndpoint("{x}", "  "));
        _catalog.DefaultOutputId = "{x}";

        AudioSnapshot output = NewService().Read(AudioFlow.Output);

        Assert.Equal(["Unnamed device"], output.Labels);
    }

    [Fact]
    public void A_default_device_that_is_not_active_is_no_default()
    {
        _catalog.DefaultOutputId = "{unplugged}";

        AudioSnapshot output = NewService().Read(AudioFlow.Output);

        Assert.Null(output.DefaultLabel);
        Assert.Null(output.DefaultId);
        Assert.Equal(3, output.Devices.Count);
    }

    [Fact]
    public void Without_a_default_device_the_label_is_null()
    {
        _catalog.DefaultInputId = null;

        Assert.Null(NewService().CurrentLabel(AudioFlow.Input));
    }

    [Fact]
    public void A_broken_audio_stack_gives_empty_devices_and_no_exception()
    {
        _catalog.ListFailure = new InvalidOperationException("The audio service is not running.");
        AudioDeviceService service = NewService();

        AudioSnapshot output = service.Read(AudioFlow.Output);

        Assert.Empty(output.Devices);
        Assert.Null(output.DefaultLabel);
        Assert.Empty(service.CurrentOptions()[SessionMetricIds.AudioOutputDevice]);
    }

    [Fact]
    public void The_options_of_the_two_metrics_are_the_labels_of_the_active_devices()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> options = NewService().CurrentOptions();

        Assert.Equal(2, options.Count);
        Assert.Equal(3, options[SessionMetricIds.AudioOutputDevice].Count);
        Assert.Equal(["Microphone (USB Headset)", "Webcam Microphone"], options[SessionMetricIds.AudioInputDevice]);
    }

    [Fact]
    public void The_metric_ids_and_the_dynamic_metrics_of_the_catalog_agree()
    {
        Assert.Equal(SessionMetricIds.AudioOutputDevice, AudioDeviceService.MetricIdOf(AudioFlow.Output));
        Assert.Equal(SessionMetricIds.AudioInputDevice, AudioDeviceService.MetricIdOf(AudioFlow.Input));
        Assert.True(DynamicSessionMetrics.Has(SessionMetricIds.AudioOutputDevice));
        Assert.True(DynamicSessionMetrics.Has(SessionMetricIds.AudioInputDevice));
        Assert.False(DynamicSessionMetrics.Has(SessionMetricIds.Volume));
        Assert.All(DynamicSessionMetrics.Ids, id => Assert.Equal("enum", SessionMetricCatalog.SessionHostSide.Single(descriptor => descriptor.Id == id).ValueType));
    }

    [Fact]
    public void The_device_metrics_are_enum_sensors_of_the_session_that_are_on_by_default_and_have_no_fixed_options()
    {
        foreach (string id in DynamicSessionMetrics.Ids)
        {
            MetricDescriptor descriptor = SessionMetricCatalog.SessionHostSide.Single(candidate => candidate.Id == id);

            Assert.Equal("sensor", descriptor.Kind);
            Assert.Equal("enum", descriptor.ValueType);
            Assert.Equal("user", descriptor.Context);
            Assert.True(descriptor.EnabledDefault);
            Assert.Null(descriptor.Options);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.TranslationKey));
            Assert.True(descriptor.IntervalSeconds > 0);
        }
    }

    // ------------------------------------------------------------------
    // The metric probe
    // ------------------------------------------------------------------

    [Fact]
    public void The_probe_reports_the_label_of_the_default_device_and_leaves_other_metrics_to_the_inner_probe()
    {
        var inner = new CountingProbe();
        inner.Set(SessionMetricIds.Volume, 40.0);
        var probe = new AudioDeviceMetricProbe(inner, NewService());

        Assert.Equal("Speakers (Realtek Audio)", probe.Read(SessionMetricIds.AudioOutputDevice));
        Assert.Equal("Microphone (USB Headset)", probe.Read(SessionMetricIds.AudioInputDevice));
        Assert.Equal(40.0, probe.Read(SessionMetricIds.Volume));
        Assert.Equal(0, inner.ReadsOf(SessionMetricIds.AudioOutputDevice));
    }

    [Fact]
    public void The_value_of_the_metric_is_always_one_of_its_options()
    {
        AudioDeviceService service = NewService();
        var probe = new AudioDeviceMetricProbe(NoUserMetricProbe.Instance, service);

        string output = Assert.IsType<string>(probe.Read(SessionMetricIds.AudioOutputDevice));
        string input = Assert.IsType<string>(probe.Read(SessionMetricIds.AudioInputDevice));

        Assert.Contains(output, service.CurrentOptions()[SessionMetricIds.AudioOutputDevice]);
        Assert.Contains(input, service.CurrentOptions()[SessionMetricIds.AudioInputDevice]);
    }

    [Fact]
    public void The_probe_is_unavailable_without_a_default_device()
    {
        _catalog.DefaultOutputId = null;
        var probe = new AudioDeviceMetricProbe(NoUserMetricProbe.Instance, NewService());

        Assert.Null(probe.Read(SessionMetricIds.AudioOutputDevice));
    }

    // ------------------------------------------------------------------
    // Notifications
    // ------------------------------------------------------------------

    [Fact]
    public void Nothing_is_watched_until_somebody_asks_and_the_notifications_end_with_the_last_watcher()
    {
        AudioDeviceService service = NewService();
        Assert.False(_catalog.IsSubscribed);

        IDisposable first = service.Watch(_ => { });
        IDisposable second = service.Watch(_ => { });
        Assert.True(_catalog.IsSubscribed);
        Assert.Equal(1, _catalog.Subscriptions);

        first.Dispose();
        Assert.True(_catalog.IsSubscribed);

        second.Dispose();
        second.Dispose();
        Assert.False(_catalog.IsSubscribed);
        Assert.Equal(1, _catalog.Unsubscriptions);
    }

    [Fact]
    public void A_device_that_appears_is_reported_as_changed_options_of_that_metric_only()
    {
        AudioDeviceService service = NewService();
        var notices = new List<MetricChangeNotice>();
        using IDisposable watch = service.Watch(notices.Add);

        _catalog.Outputs.Add(new AudioEndpoint("{new}", "Bluetooth Speaker"));
        _catalog.Raise();

        MetricChangeNotice notice = Assert.Single(notices);
        Assert.True(notice.OptionsChanged);
        Assert.Equal([SessionMetricIds.AudioOutputDevice], notice.MetricIds);
        Assert.Contains("Bluetooth Speaker", service.CurrentOptions()[SessionMetricIds.AudioOutputDevice]);
    }

    [Fact]
    public void A_device_that_disappears_is_reported_and_leaves_the_options()
    {
        AudioDeviceService service = NewService();
        var notices = new List<MetricChangeNotice>();
        using IDisposable watch = service.Watch(notices.Add);

        _catalog.Outputs.RemoveAll(endpoint => endpoint.Id == TvId);
        _catalog.Raise();

        MetricChangeNotice notice = Assert.Single(notices);
        Assert.True(notice.OptionsChanged);
        Assert.DoesNotContain("LG TV (NVIDIA High Definition Audio)", service.CurrentOptions()[SessionMetricIds.AudioOutputDevice]);
    }

    [Fact]
    public void A_new_default_device_is_reported_as_a_changed_value_without_changed_options()
    {
        AudioDeviceService service = NewService();
        var notices = new List<MetricChangeNotice>();
        using IDisposable watch = service.Watch(notices.Add);

        _catalog.DefaultOutputId = HeadphonesId;
        _catalog.Raise();

        MetricChangeNotice notice = Assert.Single(notices);
        Assert.False(notice.OptionsChanged);
        Assert.Equal([SessionMetricIds.AudioOutputDevice], notice.MetricIds);
        Assert.Equal("Headphones (USB Headset)", service.CurrentLabel(AudioFlow.Output));
    }

    [Fact]
    public void A_recording_device_is_reported_as_the_input_metric()
    {
        AudioDeviceService service = NewService();
        var notices = new List<MetricChangeNotice>();
        using IDisposable watch = service.Watch(notices.Add);

        _catalog.DefaultInputId = WebcamId;
        _catalog.Raise();

        Assert.Equal([SessionMetricIds.AudioInputDevice], Assert.Single(notices).MetricIds);
    }

    [Fact]
    public void A_notification_that_changes_nothing_is_not_reported()
    {
        AudioDeviceService service = NewService();
        var notices = new List<MetricChangeNotice>();
        using IDisposable watch = service.Watch(notices.Add);

        _catalog.Raise();
        _catalog.Raise();

        Assert.Empty(notices);
    }

    [Fact]
    public void A_burst_of_notifications_for_one_change_gives_one_report()
    {
        AudioDeviceService service = NewService();
        var notices = new List<MetricChangeNotice>();
        using IDisposable watch = service.Watch(notices.Add);

        _catalog.Outputs.Add(new AudioEndpoint("{new}", "Bluetooth Speaker"));
        _catalog.Raise();
        _catalog.Raise();
        _catalog.Raise();

        Assert.Single(notices);
    }

    [Fact]
    public void Several_changes_at_once_are_reported_together()
    {
        AudioDeviceService service = NewService();
        var notices = new List<MetricChangeNotice>();
        using IDisposable watch = service.Watch(notices.Add);

        _catalog.Outputs.Add(new AudioEndpoint("{new}", "Bluetooth Speaker"));
        _catalog.Inputs.RemoveAll(endpoint => endpoint.Id == WebcamId);
        _catalog.Raise();

        MetricChangeNotice notice = Assert.Single(notices);
        Assert.Equal(
            [SessionMetricIds.AudioOutputDevice, SessionMetricIds.AudioInputDevice],
            notice.MetricIds);
        Assert.True(notice.OptionsChanged);
    }

    [Fact]
    public void A_watcher_that_throws_does_not_keep_the_others_from_hearing_of_the_change()
    {
        AudioDeviceService service = NewService();
        var heard = new List<MetricChangeNotice>();
        using IDisposable broken = service.Watch(_ => throw new InvalidOperationException("The pipe is gone."));
        using IDisposable working = service.Watch(heard.Add);

        _catalog.DefaultOutputId = TvId;
        _catalog.Raise();

        Assert.Single(heard);
        Assert.Contains(_log.Warnings, warning => warning.Contains("The pipe is gone.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_disposed_watcher_hears_nothing_more()
    {
        AudioDeviceService service = NewService();
        var first = new List<MetricChangeNotice>();
        var second = new List<MetricChangeNotice>();
        IDisposable one = service.Watch(first.Add);
        using IDisposable two = service.Watch(second.Add);

        one.Dispose();
        _catalog.DefaultOutputId = TvId;
        _catalog.Raise();

        Assert.Empty(first);
        Assert.Single(second);
    }

    [Fact]
    public void A_registration_that_fails_is_logged_and_the_watcher_still_works_without_notifications()
    {
        _catalog.SubscribeFailure = new AudioDeviceException("The audio service did not accept the registration.");
        AudioDeviceService service = NewService();

        using IDisposable watch = service.Watch(_ => { });

        Assert.Contains(_log.Warnings, warning => warning.Contains("registration", StringComparison.Ordinal));
        Assert.Equal("Speakers (Realtek Audio)", service.CurrentLabel(AudioFlow.Output));
    }

    // ------------------------------------------------------------------
    // Matching the variable Device
    // ------------------------------------------------------------------

    private static IReadOnlyList<AudioDevice> Devices(params string[] labels) =>
        [.. labels.Select((label, index) => new AudioDevice("{" + index + "}", label))];

    [Fact]
    public void An_exact_name_is_found()
    {
        AudioMatch match = AudioDeviceMatcher.Match(Devices("Speakers", "Headset"), "Headset");

        Assert.Equal(AudioMatchKind.Exact, match.Kind);
        Assert.Equal("Headset", match.Device!.Label);
    }

    [Fact]
    public void An_exact_name_wins_over_a_longer_name_that_contains_it()
    {
        AudioMatch match = AudioDeviceMatcher.Match(Devices("Speakers", "Speakers (USB)"), "Speakers");

        Assert.Equal(AudioMatchKind.Exact, match.Kind);
        Assert.Equal("Speakers", match.Device!.Label);
    }

    [Theory]
    [InlineData("realtek")]
    [InlineData("REALTEK")]
    [InlineData("Real")]
    [InlineData("speakers (realtek")]
    public void A_part_of_one_name_is_found_whatever_its_case(string text)
    {
        AudioMatch match = AudioDeviceMatcher.Match(Devices("Speakers (Realtek Audio)", "Headphones (USB Headset)"), text);

        Assert.Equal(AudioMatchKind.UniquePart, match.Kind);
        Assert.Equal("Speakers (Realtek Audio)", match.Device!.Label);
    }

    [Fact]
    public void A_part_that_fits_several_names_is_ambiguous_and_lists_them()
    {
        AudioMatch match = AudioDeviceMatcher.Match(Devices("Headset (USB)", "Headset (Bluetooth)", "Speakers"), "headset");

        Assert.Equal(AudioMatchKind.Ambiguous, match.Kind);
        Assert.False(match.IsMatch);
        Assert.Null(match.Device);
        Assert.Equal(["Headset (USB)", "Headset (Bluetooth)"], match.Candidates);
    }

    [Fact]
    public void An_exact_name_in_the_wrong_case_is_not_exact_and_so_ambiguous_when_a_longer_name_contains_it()
    {
        AudioMatch match = AudioDeviceMatcher.Match(Devices("Speakers", "Speakers (USB)"), "speakers");

        Assert.Equal(AudioMatchKind.Ambiguous, match.Kind);
    }

    [Fact]
    public void A_name_that_fits_nothing_is_not_found()
    {
        AudioMatch match = AudioDeviceMatcher.Match(Devices("Speakers", "Headset"), "Monitor");

        Assert.Equal(AudioMatchKind.NotFound, match.Kind);
        Assert.False(match.IsMatch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_not_a_device(string? text)
    {
        Assert.Equal(AudioMatchKind.Empty, AudioDeviceMatcher.Match(Devices("Speakers"), text).Kind);
    }

    [Fact]
    public void White_space_around_the_name_is_ignored()
    {
        AudioMatch match = AudioDeviceMatcher.Match(Devices("Speakers", "Headset"), "  Headset \t");

        Assert.Equal(AudioMatchKind.Exact, match.Kind);
    }

    [Fact]
    public void Nothing_matches_when_there_are_no_devices()
    {
        Assert.Equal(AudioMatchKind.NotFound, AudioDeviceMatcher.Match([], "Speakers").Kind);
    }

    [Fact]
    public void A_numbered_label_is_an_exact_name()
    {
        IReadOnlyList<AudioDevice> devices = AudioDeviceMatcher.Label([new AudioEndpoint("{a}", "USB Headset"), new AudioEndpoint("{b}", "USB Headset")]);

        AudioMatch second = AudioDeviceMatcher.Match(devices, "USB Headset #2");

        Assert.Equal(AudioMatchKind.Exact, second.Kind);
        Assert.Equal("{b}", second.Device!.Id);
    }

    // ------------------------------------------------------------------
    // Switching the default device
    // ------------------------------------------------------------------

    [Fact]
    public void An_exact_name_sets_the_default_and_the_communication_device_of_the_playback_side()
    {
        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, "Headphones (USB Headset)");

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Contains("Headphones (USB Headset)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(
            [(HeadphonesId, AudioRole.Console), (HeadphonesId, AudioRole.Multimedia), (HeadphonesId, AudioRole.Communications)],
            _switch.Calls);
    }

    [Fact]
    public void A_unique_part_of_the_name_sets_the_default()
    {
        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, "lg tv");

        Assert.True(result.Succeeded);
        Assert.All(_switch.Calls, call => Assert.Equal(TvId, call.Id));
        Assert.Equal(3, _switch.Calls.Count);
    }

    [Fact]
    public void The_recording_side_uses_the_recording_devices()
    {
        ExecutionResult result = NewService().SetDefault(AudioFlow.Input, "webcam");

        Assert.True(result.Succeeded);
        Assert.All(_switch.Calls, call => Assert.Equal(WebcamId, call.Id));
        Assert.Contains("recording", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void A_playback_name_is_not_found_among_the_recording_devices()
    {
        ExecutionResult result = NewService().SetDefault(AudioFlow.Input, "Speakers");

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_switch.Calls);
    }

    [Fact]
    public void An_ambiguous_name_is_invalid_vars_and_changes_nothing()
    {
        // Both "Headphones (USB Headset)" and "Microphone (USB Headset)" exist, but on different sides; on one side make two.
        _catalog.Outputs.Add(new AudioEndpoint("{hs2}", "Headphones (Bluetooth)"));

        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, "headphones");

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Contains("Headphones (USB Headset)", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Headphones (Bluetooth)", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(_switch.Calls);
    }

    [Fact]
    public void An_unknown_name_is_invalid_vars_lists_the_active_devices_and_changes_nothing()
    {
        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, "Monitor");

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Contains("Speakers (Realtek Audio)", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(_switch.Calls);
    }

    [Fact]
    public void A_device_that_is_not_active_cannot_be_chosen()
    {
        _catalog.Outputs.RemoveAll(endpoint => endpoint.Id == HeadphonesId);

        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, "Headphones (USB Headset)");

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_switch.Calls);
    }

    [Fact]
    public void Without_any_active_device_the_name_is_unknown()
    {
        _catalog.Outputs.Clear();

        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, "Speakers");

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Contains("no active playback device", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_switch.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_name_is_invalid_vars_and_changes_nothing(string? device)
    {
        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, device);

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_switch.Calls);
    }

    [Fact]
    public void A_name_that_is_far_too_long_is_invalid_vars_and_changes_nothing()
    {
        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, new string('x', AudioDeviceSpec.MaxDeviceLength + 1));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_switch.Calls);
    }

    [Fact]
    public void Windows_that_refuses_the_change_gives_a_clear_failure_that_names_the_undocumented_interface()
    {
        _switch.FailOnCall = 1;
        _switch.Failure = new AudioDeviceException("Windows refused the change (error 0x80070005). The agent uses an undocumented Windows interface (IPolicyConfig).");

        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, "Headphones (USB Headset)");

        Assert.False(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Contains("could not be changed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Headphones (USB Headset)", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("0x80070005", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("undocumented", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("earlier roles", result.StandardError, StringComparison.Ordinal);
        Assert.Single(_switch.Calls);
    }

    [Fact]
    public void A_failure_after_the_first_role_says_that_earlier_roles_may_have_changed()
    {
        _switch.FailOnCall = 3;

        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, "Headphones (USB Headset)");

        Assert.False(result.Succeeded);
        Assert.Contains("earlier roles may already have changed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("communication", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexpected_exception_from_the_interface_becomes_a_failure_not_a_crash()
    {
        _switch.FailOnCall = 1;
        _switch.Failure = new InvalidOperationException("The class is not registered.");

        ExecutionResult result = NewService().SetDefault(AudioFlow.Output, "Speakers");

        Assert.False(result.Succeeded);
        Assert.Contains("The class is not registered.", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void The_metric_follows_a_successful_switch_through_the_notification()
    {
        AudioDeviceService service = NewService();
        var notices = new List<MetricChangeNotice>();
        using IDisposable watch = service.Watch(notices.Add);
        _switch.OnSet = (id, role) =>
        {
            // Windows makes the device the default and then sends its notification.
            if (role == AudioRole.Console)
            {
                _catalog.DefaultOutputId = id;
            }
        };

        ExecutionResult result = service.SetDefault(AudioFlow.Output, "Headphones");
        _catalog.Raise();

        Assert.True(result.Succeeded);
        Assert.Equal("Headphones (USB Headset)", service.CurrentLabel(AudioFlow.Output));
        Assert.Equal([SessionMetricIds.AudioOutputDevice], Assert.Single(notices).MetricIds);
    }

    // ------------------------------------------------------------------
    // The actions
    // ------------------------------------------------------------------

    private UserBuiltinActions NewActions(AudioDeviceService? devices) =>
        new(new FakeDesktopNativeApi(), new FakeAudioEndpoint(), new FakeProcessControl(), devices: devices);

    private static Task<ExecutionResult> RunAsync(UserBuiltinActions actions, string action, string device) =>
        actions.ExecuteAsync(action, ActionTestData.Vars((AudioDeviceSpec.Device, device)), CancellationToken.None).AsTask();

    [Fact]
    public async Task The_output_action_switches_the_playback_device()
    {
        UserBuiltinActions actions = NewActions(NewService());

        ExecutionResult result = await RunAsync(actions, WindowsActionNames.SetDefaultOutput, "tv");

        Assert.True(result.Succeeded);
        Assert.All(_switch.Calls, call => Assert.Equal(TvId, call.Id));
    }

    [Fact]
    public async Task The_input_action_switches_the_recording_device()
    {
        UserBuiltinActions actions = NewActions(NewService());

        ExecutionResult result = await RunAsync(actions, WindowsActionNames.SetDefaultInput, "Webcam Microphone");

        Assert.True(result.Succeeded);
        Assert.All(_switch.Calls, call => Assert.Equal(WebcamId, call.Id));
    }

    [Theory]
    [InlineData(WindowsActionNames.SetDefaultOutput, "headset")]
    [InlineData(WindowsActionNames.SetDefaultOutput, "nothing like this")]
    [InlineData(WindowsActionNames.SetDefaultInput, "microphone")]
    [InlineData(WindowsActionNames.SetDefaultInput, "Speakers")]
    public async Task An_ambiguous_or_unknown_name_is_rejected_with_invalid_vars_by_both_actions(string action, string device)
    {
        // "headset" fits "Headphones (USB Headset)" only on the output side; add a second output with that part to make it ambiguous.
        _catalog.Outputs.Add(new AudioEndpoint("{hs2}", "Headset (Bluetooth)"));
        UserBuiltinActions actions = NewActions(NewService());

        ExecutionResult result = await RunAsync(actions, action, device);

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_switch.Calls);
    }

    [Fact]
    public async Task Without_the_audio_service_the_actions_fail_with_a_message()
    {
        UserBuiltinActions actions = NewActions(devices: null);

        ExecutionResult result = await RunAsync(actions, WindowsActionNames.SetDefaultOutput, "Speakers");

        Assert.False(result.Succeeded);
        Assert.Contains("not available", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_host_offers_the_two_actions_with_the_variable_Device()
    {
        UserBuiltinActions actions = NewActions(NewService());

        foreach (string name in new[] { WindowsActionNames.SetDefaultOutput, WindowsActionNames.SetDefaultInput })
        {
            BuiltinActionDescriptor descriptor = actions.Actions.Single(action => action.Name == name);
            ParameterDefinition device = Assert.Single(descriptor.Parameters);

            Assert.Equal(RunAsValues.User, descriptor.RunAs);
            Assert.Equal(AudioDeviceSpec.Device, device.Name);
            Assert.Equal(ParameterTypes.Text, device.Type);
            Assert.True(device.Required);
        }
    }

    // ------------------------------------------------------------------
    // The default commands
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("audio_output_device", WindowsActionNames.SetDefaultOutput, SessionMetricIds.AudioOutputDevice)]
    [InlineData("audio_input_device", WindowsActionNames.SetDefaultInput, SessionMetricIds.AudioInputDevice)]
    public void The_default_commands_are_selects_with_the_device_metric_as_options_and_as_state(string id, string action, string metric)
    {
        CommandDocument command = DefaultCommandSeeder.DefaultCommands.Single(candidate => candidate.Id == id);

        Assert.Equal(action, Assert.IsType<BuiltinAction>(command.Action).Name);
        Assert.Equal(RunAsValues.User, command.RunAs);
        Assert.False(command.Enabled);
        Assert.False(command.ExposeToHa);
        ParameterDefinition device = Assert.Single(command.Parameters!);
        Assert.Equal(AudioDeviceSpec.Device, device.Name);
        Assert.Equal(ParameterTypes.Choice, device.Type);
        Assert.Equal(metric, device.OptionsFrom);
        Assert.Null(device.Options);
        Assert.Equal(HaControlTypes.Select, command.HaControl!.Type);
        Assert.Equal(AudioDeviceSpec.Device, command.HaControl.Parameter);
        Assert.Equal(metric, command.HaControl.StateMetric);
        Assert.Contains(SessionMetricCatalog.SessionHostSide, descriptor => descriptor.Id == metric);
    }

    [Fact]
    public void The_default_commands_are_valid_documents()
    {
        foreach (CommandDocument command in DefaultCommandSeeder.DefaultCommands)
        {
            ValidationResult result = DocumentValidator.ValidateCommand(command);

            Assert.True(result.IsValid, command.Id + ": " + string.Join("; ", result.Errors.Select(error => error.Path + " " + error.Message)));
        }
    }

    // ------------------------------------------------------------------
    // Over the session pipe: options, value and notifications reach the agent
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_devices_reach_the_agent_after_the_handshake_as_the_options_of_the_two_metrics()
    {
        AudioDeviceService service = NewService();
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost(metrics: new AudioDeviceMetricProbe(NoUserMetricProbe.Instance, service), changes: service);
        await harness.NextSessionReadyAsync();

        await TestWait.UntilAsync(() => OptionsOf(harness, SessionMetricIds.AudioOutputDevice) is { Count: 3 }, "the playback devices");

        Assert.Equal(
            ["Headphones (USB Headset)", "LG TV (NVIDIA High Definition Audio)", "Speakers (Realtek Audio)"],
            OptionsOf(harness, SessionMetricIds.AudioOutputDevice));
        Assert.Equal(["Microphone (USB Headset)", "Webcam Microphone"], OptionsOf(harness, SessionMetricIds.AudioInputDevice));
        Assert.Equal("enum", harness.Bridge.DescribeUserMetrics().Single(descriptor => descriptor.Id == SessionMetricIds.AudioOutputDevice).ValueType);
    }

    [Fact]
    public async Task A_plugged_in_device_becomes_an_option_without_restarting_anything()
    {
        AudioDeviceService service = NewService();
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost(metrics: new AudioDeviceMetricProbe(NoUserMetricProbe.Instance, service), changes: service);
        await harness.NextSessionReadyAsync();
        await TestWait.UntilAsync(() => OptionsOf(harness, SessionMetricIds.AudioOutputDevice) is { Count: 3 }, "the playback devices");
        int changes = 0;
        harness.Bridge.UserMetricsChanged += (_, _) => Interlocked.Increment(ref changes);

        _catalog.Outputs.Add(new AudioEndpoint("{bt}", "Bluetooth Speaker"));
        _catalog.Raise();

        await TestWait.UntilAsync(() => OptionsOf(harness, SessionMetricIds.AudioOutputDevice) is { Count: 4 }, "the new device");
        Assert.Contains("Bluetooth Speaker", OptionsOf(harness, SessionMetricIds.AudioOutputDevice)!);
        Assert.True(Volatile.Read(ref changes) >= 1, "The bridge must tell the metric engine, so that the manifest is built again.");
    }

    [Fact]
    public async Task A_device_that_is_unplugged_leaves_the_options()
    {
        AudioDeviceService service = NewService();
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost(metrics: new AudioDeviceMetricProbe(NoUserMetricProbe.Instance, service), changes: service);
        await harness.NextSessionReadyAsync();
        await TestWait.UntilAsync(() => OptionsOf(harness, SessionMetricIds.AudioOutputDevice) is { Count: 3 }, "the playback devices");

        _catalog.Outputs.RemoveAll(endpoint => endpoint.Id == TvId);
        _catalog.Raise();

        await TestWait.UntilAsync(() => OptionsOf(harness, SessionMetricIds.AudioOutputDevice) is { Count: 2 }, "the device to go");
        Assert.DoesNotContain("LG TV (NVIDIA High Definition Audio)", OptionsOf(harness, SessionMetricIds.AudioOutputDevice)!);
    }

    [Fact]
    public async Task A_new_default_device_reaches_the_agent_at_once_not_at_the_next_interval()
    {
        AudioDeviceService service = NewService();
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost(metrics: new AudioDeviceMetricProbe(NoUserMetricProbe.Instance, service), changes: service);
        await harness.NextSessionReadyAsync();

        // The agent subscribes by sampling; the interval of the metric is 30 s.
        IReadOnlyDictionary<string, object?> first = await harness.Bridge.SampleUserMetricsAsync([SessionMetricIds.AudioOutputDevice], CancellationToken.None);
        Assert.Equal("Speakers (Realtek Audio)", first[SessionMetricIds.AudioOutputDevice]);

        _catalog.DefaultOutputId = HeadphonesId;
        _catalog.Raise();

        await TestWait.UntilAsync(
            () => harness.Bridge.SampleUserMetricsAsync([SessionMetricIds.AudioOutputDevice], CancellationToken.None).AsTask().GetAwaiter().GetResult()[SessionMetricIds.AudioOutputDevice] is "Headphones (USB Headset)",
            "the new default device",
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_metric_that_nobody_subscribed_is_not_read_when_the_devices_change()
    {
        var inner = new CountingProbe();
        AudioDeviceService service = NewService();
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost(metrics: new AudioDeviceMetricProbe(inner, service), changes: service);
        await harness.NextSessionReadyAsync();
        await TestWait.UntilAsync(() => OptionsOf(harness, SessionMetricIds.AudioOutputDevice) is { Count: 3 }, "the playback devices");

        _catalog.DefaultOutputId = HeadphonesId;
        _catalog.Raise();
        await Task.Delay(300);

        // The options are still sent (the manifest needs them); the value is not sampled because nobody asked for it.
        Assert.Equal(0, inner.TotalReads);
    }

    [Fact]
    public async Task The_options_leave_the_manifest_when_the_session_host_goes_away_and_the_notifications_end()
    {
        AudioDeviceService service = NewService();
        using var harness = new SessionHarness();
        await harness.StartAsync();
        RunningHost host = harness.StartHost(metrics: new AudioDeviceMetricProbe(NoUserMetricProbe.Instance, service), changes: service);
        await harness.NextSessionReadyAsync();
        await TestWait.UntilAsync(() => OptionsOf(harness, SessionMetricIds.AudioOutputDevice) is { Count: 3 }, "the playback devices");
        Assert.True(_catalog.IsSubscribed);

        await host.StopAsync();
        host.Dispose();

        await TestWait.UntilAsync(() => OptionsOf(harness, SessionMetricIds.AudioOutputDevice) is null, "the options to go with the session helper");
        await TestWait.UntilAsync(() => !_catalog.IsSubscribed, "the notifications to end");
    }

    [Fact]
    public async Task A_session_host_without_audio_devices_gives_metrics_without_options()
    {
        _catalog.Outputs.Clear();
        _catalog.Inputs.Clear();
        _catalog.DefaultOutputId = null;
        _catalog.DefaultInputId = null;
        AudioDeviceService service = NewService();
        using var harness = new SessionHarness();
        await harness.StartAsync();
        using RunningHost host = harness.StartHost(metrics: new AudioDeviceMetricProbe(NoUserMetricProbe.Instance, service), changes: service);
        await harness.NextSessionReadyAsync();
        await Task.Delay(300);

        Assert.Null(OptionsOf(harness, SessionMetricIds.AudioOutputDevice));
        Assert.Null((await harness.Bridge.SampleUserMetricsAsync([SessionMetricIds.AudioOutputDevice], CancellationToken.None))[SessionMetricIds.AudioOutputDevice]);
    }

    private static IReadOnlyList<string>? OptionsOf(SessionHarness harness, string metricId)
    {
        return harness.Bridge.DescribeUserMetrics().Single(descriptor => descriptor.Id == metricId).Options;
    }

    // ------------------------------------------------------------------
    // The real Windows (the COM classes); skipped elsewhere
    // ------------------------------------------------------------------

    [WindowsFact]
    public void The_real_catalog_lists_devices_and_reads_the_default_without_throwing()
    {
        var catalog = new WindowsAudioEndpointCatalog();

        foreach (AudioFlow flow in new[] { AudioFlow.Output, AudioFlow.Input })
        {
            IReadOnlyList<AudioEndpoint> endpoints = catalog.GetActiveEndpoints(flow);
            string? defaultId = catalog.GetDefaultEndpointId(flow);

            // A build agent without a sound card has no devices; the result is then empty, never an exception.
            Assert.All(endpoints, endpoint => Assert.False(string.IsNullOrEmpty(endpoint.Id)));
            if (defaultId is not null)
            {
                Assert.False(string.IsNullOrWhiteSpace(defaultId));
            }
        }
    }

    [WindowsFact]
    public void The_real_catalog_registers_and_releases_the_notifications()
    {
        var catalog = new WindowsAudioEndpointCatalog();

        Exception? failure = Record.Exception(() =>
        {
            using IDisposable subscription = catalog.Subscribe(() => { });
        });

        // Without an audio service the registration is refused; that is a clear AudioDeviceException, not a COM crash.
        Assert.True(failure is null or AudioDeviceException, failure?.ToString());
    }

    [WindowsFact]
    public void The_real_policy_config_refuses_a_device_that_does_not_exist_with_a_clear_error_and_changes_nothing()
    {
        var policy = new PolicyConfigEndpointSwitch();

        AudioDeviceException failure = Assert.Throws<AudioDeviceException>(
            () => policy.SetDefault("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}", AudioRole.Console));

        Assert.Contains("0x", failure.Message, StringComparison.Ordinal);
        Assert.Contains("undocumented", failure.Message, StringComparison.Ordinal);
    }
}
