using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Core.Platform;

/// <summary>
/// Reports what became of the optional hardware sensors (PLAN.md D-045, ADR 0002, T707): whether they run, whether the kernel driver that
/// they need is installed and how many sensors were found. The Windows platform implements it; on other platforms
/// <see cref="NoHardwareSensorStatusSource"/> says that hardware sensors are not supported. The management API shows the result so that
/// the UI can warn about the driver and explain why nothing is read.
/// </summary>
public interface IHardwareSensorStatusSource
{
    /// <summary>Describes the current state. Never loads the hardware library or the driver and never blocks.</summary>
    /// <returns>The status.</returns>
    HardwareSensorStatus GetStatus();
}

/// <summary>The status source of a platform without hardware sensors.</summary>
public sealed class NoHardwareSensorStatusSource : IHardwareSensorStatusSource
{
    /// <summary>The name of the driver that the Windows implementation needs; shown by the UI even where it is not supported.</summary>
    public const string DriverName = "PawnIO";

    /// <summary>The only instance.</summary>
    public static NoHardwareSensorStatusSource Instance { get; } = new();

    /// <inheritdoc />
    public HardwareSensorStatus GetStatus()
    {
        return new HardwareSensorStatus(false, false, HardwareSensorStates.Unsupported, DriverName, null, 0);
    }
}
