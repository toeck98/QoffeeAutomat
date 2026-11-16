using System.Globalization;

namespace QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;

/// <summary>
/// When this computer started (PLAN.md S06, S07, T304): the source of the metric <c>system.last_boot</c> and of the boot id
/// that the agent announces in <c>hello</c>. The start time is the current time minus the uptime; two measures keep it from
/// jittering with the clock. The <see cref="BootId"/> is fixed when the agent starts (an agent restart keeps it, an operating
/// system start changes it, and a later adjustment of the clock by the time service does not), and <see cref="BootTime"/> only
/// follows the clock when it differs by more than <see cref="Tolerance"/>, so a correction that the time service makes after
/// the start is picked up while the normal measurement noise is not published.
/// </summary>
public sealed class WindowsBootTime
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
    /// <param name="uptime">The time since the computer started; null reads the tick count of Windows.</param>
    public WindowsBootTime(TimeProvider time, Func<TimeSpan>? uptime = null)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        _uptime = uptime ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
        _published = Measure();
        _bootId = "win-" + (_published.ToUnixTimeSeconds() / 60).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>An id that changes with every start of the operating system, whole minutes since 1970 of the start prefixed with <c>win-</c>.</summary>
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
