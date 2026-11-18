using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Platform.Linux.Metrics.Session;
using QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

namespace QoffeeAutomat.Platform.Linux.Tests.Metrics;

/// <summary>
/// The providers on the real computer (PLAN.md S06, T801, acceptance "providers return plausible values on ubuntu-latest"). They
/// read the real <c>/proc</c>, <c>/sys</c> and logind of the machine that runs the tests, so they assert ranges and shapes, not
/// values, and they accept that optional hardware (a battery, a network adapter, a login session) is not there. On other
/// operating systems they are skipped. Whether a machine has local volumes is only demanded on GitHub Actions, where it is known.
/// </summary>
[Trait("Category", "LinuxHost")]
public sealed class LinuxHostTests
{
    private static bool OnGitHubActions => string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal);

    private static T Readable<T>(T? value, string what)
        where T : struct
    {
        return value ?? throw new Xunit.Sdk.XunitException($"{what} of this computer could not be read.");
    }

    [LinuxHostFact]
    public void The_memory_of_this_computer_is_plausible()
    {
        MemoryStatus memory = Readable(new LinuxSystemSources().ReadMemory(), "The memory");

        Assert.True(memory.TotalBytes > 64UL * 1024 * 1024, "less than 64 MiB of memory");
        Assert.True(memory.AvailableBytes > 0);
        Assert.True(memory.AvailableBytes <= memory.TotalBytes);
    }

    [LinuxHostFact]
    public void The_processor_times_of_this_computer_are_plausible_and_grow()
    {
        var sources = new LinuxSystemSources();

        CpuTimes first = Readable(sources.ReadCpuTimes(), "The processor times");
        Thread.Sleep(50);
        CpuTimes second = Readable(sources.ReadCpuTimes(), "The processor times");

        Assert.True(first.Total > 0);
        Assert.True(first.Idle <= first.Total);
        Assert.True(second.Total >= first.Total);
    }

    [LinuxHostFact]
    public void The_uptime_the_boot_id_and_the_distribution_of_this_computer_are_plausible()
    {
        var sources = new LinuxSystemSources();

        TimeSpan uptime = sources.ReadUptime();
        string? osVersion = sources.ReadOsVersion();
        string? bootId = sources.ReadKernelBootId();

        Assert.True(uptime > TimeSpan.Zero);
        Assert.True(uptime < TimeSpan.FromDays(365 * 20));
        Assert.False(string.IsNullOrWhiteSpace(osVersion));
        Assert.Contains("Linux", osVersion, StringComparison.OrdinalIgnoreCase);
        Assert.True(Guid.TryParse(bootId, out _), $"The kernel boot id '{bootId}' is not a GUID.");
    }

    [LinuxHostFact]
    public void The_volumes_of_this_computer_are_plausible()
    {
        IReadOnlyList<VolumeStatus> volumes = new LinuxSystemSources().ReadVolumes();

        if (OnGitHubActions)
        {
            Assert.NotEmpty(volumes);
        }

        Assert.All(volumes, volume =>
        {
            Assert.StartsWith("/", volume.MountPoint, StringComparison.Ordinal);
            Assert.True(volume.TotalBytes > 0, volume.MountPoint);
            Assert.InRange(volume.FreeBytes, 0, volume.TotalBytes);
        });
        Assert.Equal(volumes.Count, volumes.Select(volume => volume.MountPoint).Distinct(StringComparer.Ordinal).Count());
    }

    [LinuxHostFact]
    public void The_network_adapters_and_the_primary_address_of_this_computer_are_plausible_or_absent()
    {
        var sources = new LinuxSystemSources();

        IReadOnlyList<NetworkAdapterStatus> adapters = sources.ReadAdapters();
        string? address = sources.ReadPrimaryIPv4();

        Assert.All(adapters, adapter =>
        {
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
            Assert.True(adapter.ReceivedBytes >= 0);
            Assert.True(adapter.SentBytes >= 0);
            Assert.NotEqual("lo", adapter.Name);
        });
        if (address is not null)
        {
            Assert.Equal(AddressFamily.InterNetwork, IPAddress.Parse(address).AddressFamily);
        }
    }

    [LinuxHostFact]
    public void A_missing_battery_is_null_and_a_present_one_has_a_level_in_range()
    {
        BatteryStatus? battery = new LinuxSystemSources().ReadBattery();

        if (battery?.LevelPercent is { } level)
        {
            Assert.InRange(level, 0, 100);
        }
    }

    [LinuxHostFact]
    public void The_pending_reboot_marker_can_be_read_without_error()
    {
        bool? pending = new LinuxSystemSources().ReadPendingReboot();

        // /run exists on every system that runs these tests, so the answer is a bool and not "unknown".
        Assert.NotNull(pending);
    }

    [LinuxHostFact]
    public async Task The_whole_catalog_samples_plausible_values_on_this_computer()
    {
        using var provider = new LinuxSystemMetricProvider(
            new LinuxSystemSources(),
            TimeProvider.System,
            options: new LinuxSystemMetricOptions { RefreshInterval = Timeout.InfiniteTimeSpan, RateWarmup = TimeSpan.FromMilliseconds(150) });
        IReadOnlyList<MetricDescriptor> descriptors = provider.Describe();
        string[] ids = [.. descriptors.Select(descriptor => descriptor.Id)];

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(ids, CancellationToken.None);

        Assert.All(ids, id => Assert.True(values.ContainsKey(id), $"No value for {id}."));

        // The battery metrics come as a pair, and only on a computer that has a battery.
        Assert.Equal(ids.Contains("battery.level"), ids.Contains("battery.charging"));

        foreach (MetricDescriptor descriptor in descriptors)
        {
            object? value = values[descriptor.Id];
            if (value is double number)
            {
                Assert.True(double.IsFinite(number) && number >= 0, $"{descriptor.Id} = {number}");
            }

            if (descriptor.Unit == "%" && value is double percent)
            {
                Assert.InRange(percent, 0, 100);
            }
        }

        // The memory is always readable; the CPU needs a busy-time difference that a very quiet machine could round to nothing.
        Assert.True(Assert.IsType<double>(values["memory.total"]) > 0);
        double usedPercent = Assert.IsType<double>(values["memory.used_percent"]);
        Assert.InRange(usedPercent, 0.01, 100);
        Assert.True(Assert.IsType<double>(values["system.uptime"]) > 0);
        DateTimeOffset lastBoot = Assert.IsType<DateTimeOffset>(values["system.last_boot"]);
        Assert.InRange(lastBoot, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(values["system.os_version"])));
        _ = Assert.IsType<bool>(values["system.pending_reboot"]);
    }

    [LinuxHostFact]
    public async Task The_session_metrics_work_on_a_computer_with_or_without_a_login()
    {
        var provider = new LinuxSessionMetricProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(["session.user", "session.locked"], timeout.Token);

        object? user = values["session.user"];
        object? locked = values["session.locked"];
        if (user is null)
        {
            // Nobody at the computer (a CI runner, a server): both are unavailable, neither is a made-up value.
            Assert.Null(locked);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(user)));
            Assert.True(locked is null or bool);
        }
    }

    [LinuxHostFact]
    public async Task The_session_files_of_logind_can_be_listed_or_are_absent()
    {
        IReadOnlyList<LogindSession>? sessions = await new SessionFilesLogindSource().ListSessionsAsync(CancellationToken.None);

        Assert.All(sessions ?? [], session => Assert.False(string.IsNullOrWhiteSpace(session.UserName)));
    }

    // ------------------------------------------------------------------
    // The process reader that loginctl runs through
    // ------------------------------------------------------------------

    [LinuxHostFact]
    public async Task The_process_reader_returns_the_output_and_passes_arguments_without_a_shell()
    {
        var reader = new ProcessOutputReader();

        string? output = await reader.ReadStandardOutputAsync("echo", ["$HOME;", "`id`", "a b"], TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("$HOME; `id` a b\n", output);
    }

    [LinuxHostFact]
    public async Task The_process_reader_gives_null_for_a_failure_and_for_a_program_that_does_not_exist()
    {
        var reader = new ProcessOutputReader();

        Assert.Null(await reader.ReadStandardOutputAsync("false", [], TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Null(await reader.ReadStandardOutputAsync("qoffeeautomat-no-such-program", [], TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [LinuxHostFact]
    public async Task The_process_reader_kills_a_program_that_runs_too_long()
    {
        var reader = new ProcessOutputReader();
        var watch = Stopwatch.StartNew();

        string? output = await reader.ReadStandardOutputAsync("sleep", ["30"], TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(output);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Took {watch.Elapsed}.");
    }

    [LinuxHostFact]
    public async Task The_process_reader_stops_when_it_is_cancelled()
    {
        var reader = new ProcessOutputReader();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await reader.ReadStandardOutputAsync("sleep", ["30"], TimeSpan.FromSeconds(60), cancellation.Token));
    }
}
