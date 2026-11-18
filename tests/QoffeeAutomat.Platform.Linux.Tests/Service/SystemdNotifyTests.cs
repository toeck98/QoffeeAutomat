using System.Net.Sockets;
using System.Text;
using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Tests.Service;

/// <summary>The notification protocol of systemd, the environment it describes and the host lifetime built on them (T802).</summary>
[Trait("Category", "Service")]
public sealed class SystemdNotifyTests
{
    [Theory]
    [InlineData("/run/systemd/notify", "/run/systemd/notify")]
    [InlineData("@abstract-name", "\0abstract-name")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("@", null)]
    [InlineData("vsock:2:1234", null)]
    [InlineData("relative/path", null)]
    public void The_notify_socket_value_becomes_an_endpoint_address(string? value, string? expected)
    {
        Assert.Equal(expected, SystemdNotifier.ToEndPointAddress(value));
    }

    [Fact]
    public void A_notifier_without_a_socket_is_disabled_and_sends_nothing()
    {
        var notifier = SystemdNotifier.FromEnvironment(_ => null);

        Assert.False(notifier.IsEnabled);
        Assert.False(notifier.Notify(SystemdStates.Ready));
    }

    [Fact]
    public void The_states_are_the_assignments_of_sd_notify()
    {
        Assert.Equal("READY=1", SystemdStates.Ready);
        Assert.Equal("STOPPING=1", SystemdStates.Stopping);
        Assert.Equal("WATCHDOG=1", SystemdStates.Watchdog);
        Assert.Equal("STATUS=a b c", SystemdStates.Status("a\nb\r\nc"));
    }

    [LinuxHostFact]
    public void A_message_arrives_as_one_datagram_on_the_socket_of_a_path()
    {
        string path = Path.Combine(Path.GetTempPath(), "qa-notify-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            using Socket listener = Bind(new UnixDomainSocketEndPoint(path));
            var notifier = SystemdNotifier.FromEnvironment(name => name == SystemdEnvironment.NotifySocketVariable ? path : null);

            Assert.True(notifier.IsEnabled);
            Assert.True(notifier.Notify(SystemdStates.Ready + "\n" + SystemdStates.Status("Running")));
            Assert.Equal("READY=1\nSTATUS=Running", Receive(listener));

            Assert.True(notifier.Notify(SystemdStates.Watchdog));
            Assert.Equal("WATCHDOG=1", Receive(listener));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [LinuxHostFact]
    public void A_message_arrives_on_a_socket_of_the_abstract_namespace()
    {
        string name = "qa-notify-" + Guid.NewGuid().ToString("N")[..10];
        using Socket listener = Bind(new UnixDomainSocketEndPoint("\0" + name));
        var notifier = new SystemdNotifier("@" + name);

        Assert.True(notifier.Notify(SystemdStates.Stopping));
        Assert.Equal("STOPPING=1", Receive(listener));
    }

    [LinuxHostFact]
    public void A_socket_that_is_gone_makes_the_notification_fail_without_an_exception()
    {
        var notifier = new SystemdNotifier(Path.Combine(Path.GetTempPath(), "qa-notify-missing-" + Guid.NewGuid().ToString("N")));

        Assert.True(notifier.IsEnabled);
        Assert.False(notifier.Notify(SystemdStates.Ready));
    }

    [Fact]
    public void A_notify_socket_marks_the_process_as_a_systemd_service()
    {
        Assert.True(SystemdEnvironment.IsSystemdService(name => name == SystemdEnvironment.NotifySocketVariable ? "/run/systemd/notify" : null));
        Assert.False(SystemdEnvironment.IsSystemdService(_ => null, Path.Combine(Path.GetTempPath(), "qa-no-root-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void The_invocation_id_counts_only_when_systemd_is_the_parent_process()
    {
        using var root = new FixtureRoot();
        string? Variables(string name) => name == SystemdEnvironment.InvocationIdVariable ? "0123456789abcdef" : null;

        // A terminal that is itself a systemd unit hands the invocation id to the shell: the parent is not process 1.
        root.Write("proc/self/stat", "4242 (dotnet) S 3100 4242 3100 34816 4242 4194304 100 0 0 0 1 1 0 0 20 0 8 0 100 1000 100 18446744073709551615");
        Assert.False(SystemdEnvironment.IsSystemdService(Variables, root.Root));

        root.Write("proc/self/stat", "4242 (dotnet) S 1 4242 4242 0 -1 4194560 100 0 0 0 1 1 0 0 20 0 8 0 100 1000 100 18446744073709551615");
        Assert.True(SystemdEnvironment.IsSystemdService(Variables, root.Root));
    }

    [Theory]
    [InlineData("1 (systemd) S 0 1 1 0", 0)]
    [InlineData("77 (a b) c) S 55 77", 55)]
    [InlineData("77 (x) R 1 77", 1)]
    public void The_parent_process_is_read_from_the_stat_line_whatever_the_command_name_contains(string stat, int expected)
    {
        Assert.Equal(expected, SystemdEnvironment.ParseParentProcessId(stat));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no parentheses here")]
    [InlineData("1 (x)")]
    [InlineData("1 (x) S notanumber")]
    public void A_stat_line_in_another_form_has_no_parent(string stat)
    {
        Assert.Null(SystemdEnvironment.ParseParentProcessId(stat));
    }

    [Fact]
    public void Systemd_is_the_init_system_when_the_runtime_directory_exists()
    {
        using var root = new FixtureRoot();
        Assert.False(SystemdEnvironment.IsSystemdBooted(root.Root));

        root.CreateDirectory("run/systemd/system");
        Assert.True(SystemdEnvironment.IsSystemdBooted(root.Root));
    }

    [Theory]
    [InlineData("60000000", null, 60.0)]
    [InlineData("60000000", "4242", 60.0)]
    [InlineData("1500000", "", 1.5)]
    public void The_watchdog_interval_is_read_from_microseconds(string usec, string? pid, double expectedSeconds)
    {
        TimeSpan? interval = SystemdEnvironment.ReadWatchdogInterval(WatchdogVariables(usec, pid), processId: 4242);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("0", null)]
    [InlineData("abc", null)]
    [InlineData("-5", null)]
    [InlineData("60000000", "1")]
    [InlineData("60000000", "notanumber")]
    public void The_watchdog_is_off_without_an_interval_or_for_another_process(string? usec, string? pid)
    {
        Assert.Null(SystemdEnvironment.ReadWatchdogInterval(WatchdogVariables(usec, pid), processId: 4242));
    }

    [Fact]
    public void Keep_alives_go_out_at_half_the_watchdog_interval_but_at_least_every_second()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SystemdEnvironment.KeepAliveInterval(TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(1), SystemdEnvironment.KeepAliveInterval(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void The_watchdog_sends_a_keep_alive_when_it_starts()
    {
        var notifier = new RecordingNotifier();
        using var watchdog = new SystemdWatchdog(notifier, TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromSeconds(30), watchdog.Interval);
        watchdog.Start();
        watchdog.Start();

        Assert.Equal([SystemdStates.Watchdog], notifier.Messages);
        Assert.True(watchdog.Ping());
        Assert.Equal(2, notifier.Messages.Count);
    }

    [LinuxHostFact]
    public async Task The_lifetime_reports_ready_when_the_host_has_started_and_stopping_when_it_begins_to_stop()
    {
        var notifier = new RecordingNotifier();
        using var application = new FakeApplicationLifetime();
        using var lifetime = new SystemdHostLifetime(notifier, application);

        await lifetime.WaitForStartAsync(CancellationToken.None);
        Assert.Empty(notifier.Messages);

        application.RaiseStarted();
        Assert.Equal(["READY=1\nSTATUS=Running"], notifier.Messages);

        application.StopApplication();
        Assert.Equal(["READY=1\nSTATUS=Running", "STOPPING=1\nSTATUS=Stopping"], notifier.Messages);
        await lifetime.StopAsync(CancellationToken.None);
    }

    [LinuxHostFact]
    public async Task The_lifetime_starts_the_watchdog_after_ready_when_systemd_asked_for_one()
    {
        var notifier = new RecordingNotifier();
        using var application = new FakeApplicationLifetime();
        using var lifetime = new SystemdHostLifetime(notifier, application, TimeSpan.FromMinutes(1));

        await lifetime.WaitForStartAsync(CancellationToken.None);
        application.RaiseStarted();

        Assert.Equal(["READY=1\nSTATUS=Running", SystemdStates.Watchdog], notifier.Messages);
    }

    private static Func<string, string?> WatchdogVariables(string? usec, string? pid) => name => name switch
    {
        SystemdEnvironment.WatchdogUsecVariable => usec,
        SystemdEnvironment.WatchdogPidVariable => pid,
        _ => null,
    };

    private static Socket Bind(UnixDomainSocketEndPoint endPoint)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified)
        {
            ReceiveTimeout = 5000,
        };
        socket.Bind(endPoint);
        return socket;
    }

    private static string Receive(Socket socket)
    {
        var buffer = new byte[256];
        int length = socket.Receive(buffer);
        return Encoding.UTF8.GetString(buffer, 0, length);
    }
}
