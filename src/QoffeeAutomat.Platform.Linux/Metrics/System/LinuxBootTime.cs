using System.Globalization;

namespace QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

/// <summary>
/// When this computer started (PLAN.md S06, S07, T801): the source of the metric <c>system.last_boot</c> and of the boot id that
/// the agent announces in <c>hello</c>. Works like <c>WindowsBootTime</c>: the start time is the current time minus the uptime,
/// <see cref="BootTime"/> only follows the clock when it differs by more than <see cref="Tolerance"/>, and the
/// <see cref="BootId"/> is fixed when the object is created. The kernel assigns an identifier to every start
/// (<c>/proc/sys/kernel/random/boot_id</c>); it is used when there is one, so that a clock that the time service corrects after
/// the start does not change the id.
/// </summary>
public sealed class LinuxBootTime
{
    /// <summary>The difference to the published start time below which a new measurement is ignored.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _uptime;
    private readonly string _bootId;
    private DateTimeOffset _published;

    /// <summary>Creates the boot time and measures it once.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="uptime">The time since the computer started.</param>
    /// <param name="kernelBootId">The identifier of the kernel for this start; null or empty derives the id from the start time.</param>
    public LinuxBootTime(TimeProvider time, Func<TimeSpan> uptime, string? kernelBootId = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(uptime);

        _time = time;
        _uptime = uptime;
        _published = Measure();
        _bootId = "linux-" + (string.IsNullOrWhiteSpace(kernelBootId)
            ? (_published.ToUnixTimeSeconds() / 60).ToString(CultureInfo.InvariantCulture)
            : kernelBootId.Trim());
    }

    /// <summary>An id that changes with every start of the operating system: <c>linux-</c> and the identifier of the kernel, or the whole minutes since 1970 of the start.</summary>
    public string BootId => _bootId;

    /// <summary>The start time in UTC, rounded to the second.</summary>
    public DateTimeOffset BootTime
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset measured = Measure();
                if ((measured - _published).Duration() > Tolerance)
                {
                    _published = measured;
                }

                return _published;
            }
        }
    }

    private DateTimeOffset Measure()
    {
        DateTimeOffset boot = _time.GetUtcNow() - _uptime();
        return DateTimeOffset.FromUnixTimeSeconds(boot.ToUnixTimeSeconds());
    }
}
