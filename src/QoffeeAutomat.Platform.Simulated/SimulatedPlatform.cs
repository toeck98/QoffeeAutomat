using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Platform.Simulated;

/// <summary>Options of <see cref="SimulatedPlatform"/>.</summary>
/// <param name="Hostname">The host name to report.</param>
/// <param name="MacAddresses">The MAC addresses; null uses one fixed address.</param>
/// <param name="AgentIdFile">The file that keeps the agent id; null keeps a fixed id in memory.</param>
/// <param name="FileOperations">The file system operations; null uses the real file system.</param>
public sealed record SimulatedPlatformOptions(
    string Hostname = "SIM-PC",
    IReadOnlyList<string>? MacAddresses = null,
    string? AgentIdFile = null,
    IFileOperations? FileOperations = null);

/// <summary>
/// All simulated providers wired to one <see cref="TimeProvider"/> (PLAN.md D-021). The agent
/// registers each property under its interface; tests keep the concrete types to flip the switches.
/// </summary>
public sealed class SimulatedPlatform : IDisposable
{
    /// <summary>Creates the platform.</summary>
    /// <param name="time">The clock; pass a <see cref="SimulatedClock"/> to control time.</param>
    /// <param name="options">Options; null uses the defaults.</param>
    public SimulatedPlatform(TimeProvider time, SimulatedPlatformOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        SimulatedPlatformOptions settings = options ?? new SimulatedPlatformOptions();

        Time = time;
        Metrics = new SimulatedMetricProvider(time);
        Actions = new SimulatedBuiltinActions();
        Session = new SimulatedSessionBridge(time);
        Power = new SimulatedPowerEvents();
        Advertiser = new SimulatedServiceAdvertiser();
        ElevatedLauncher = new SimulatedElevatedLauncher(Session);
        Info = new SimulatedPlatformInfo(settings.Hostname, settings.MacAddresses, settings.AgentIdFile, settings.FileOperations);
    }

    /// <summary>The clock that drives every provider.</summary>
    public TimeProvider Time { get; }

    /// <summary>The metrics.</summary>
    public SimulatedMetricProvider Metrics { get; }

    /// <summary>The built-in actions.</summary>
    public SimulatedBuiltinActions Actions { get; }

    /// <summary>The session bridge.</summary>
    public SimulatedSessionBridge Session { get; }

    /// <summary>The power events.</summary>
    public SimulatedPowerEvents Power { get; }

    /// <summary>The service advertiser.</summary>
    public SimulatedServiceAdvertiser Advertiser { get; }

    /// <summary>The launcher for elevated user processes.</summary>
    public SimulatedElevatedLauncher ElevatedLauncher { get; }

    /// <summary>The platform facts.</summary>
    public SimulatedPlatformInfo Info { get; }

    /// <summary>Releases the platform info.</summary>
    public void Dispose()
    {
        Info.Dispose();
    }
}
