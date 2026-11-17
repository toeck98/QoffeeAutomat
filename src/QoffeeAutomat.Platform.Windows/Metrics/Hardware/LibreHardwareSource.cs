using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using LibreHardwareMonitor.Hardware;

namespace QoffeeAutomat.Platform.Windows.Metrics.HardwareSensors;

/// <summary>
/// Opens LibreHardwareMonitorLib (MPL-2.0, ADR 0002). It is the only type of the agent that names a type of the library besides
/// <see cref="LibreHardwareSource"/>, and it does so only inside <see cref="Open"/>: creating the factory, which the container does at
/// start, does not load the library; calling <see cref="Open"/> does.
/// </summary>
public sealed class LibreHardwareSourceFactory : IHardwareSourceFactory
{
    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IHardwareSource Open() => LibreHardwareSource.Create();
}

/// <summary>
/// The hardware as LibreHardwareMonitorLib sees it: processor, graphics cards, mainboard (with Super I/O and embedded controller) and
/// drives. Memory, network and the other hardware types are not enabled, because the metrics offer temperatures, fans and power draw only.
/// </summary>
internal sealed class LibreHardwareSource : IHardwareSource
{
    private readonly Computer _computer;
    private bool _disposed;

    private LibreHardwareSource(Computer computer)
    {
        _computer = computer;
    }

    /// <summary>
    /// Checks the library version, then opens the hardware, which is the moment the library connects to the kernel driver. If the
    /// library nevertheless creates the legacy WinRing0 service, it is closed again at once and the call fails.
    /// </summary>
    /// <returns>The open source.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static LibreHardwareSource Create()
    {
        Assembly library = typeof(Computer).Assembly;
        Version? version = HardwareLibraryGuard.ParseVersion(
            library.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            library.GetName().Version);
        if (!HardwareLibraryGuard.IsAcceptable(version))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"LibreHardwareMonitorLib {version?.ToString() ?? "(unknown version)"} is older than {HardwareLibraryGuard.MinimumLibraryVersion}, the first release without the WinRing0 driver. The hardware sensors stay off."));
        }

        bool legacyBefore = HardwareLibraryGuard.LegacyDriverServiceExists();
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
        };

        try
        {
            computer.Open();
            if (!legacyBefore && HardwareLibraryGuard.LegacyDriverServiceExists())
            {
                throw new InvalidOperationException("The library registered the legacy WinRing0 driver service (" + HardwareLibraryGuard.LegacyDriverServiceName + "). It was closed again and the hardware sensors stay off; see ADR 0002.");
            }

            return new LibreHardwareSource(computer);
        }
        catch
        {
            computer.Close();
            throw;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HardwareSensorReading> Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var readings = new List<HardwareSensorReading>();
        foreach (IHardware hardware in _computer.Hardware)
        {
            Collect(hardware, CategoryOf(hardware.HardwareType), readings);
        }

        readings.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return readings;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closes every part of the hardware and the connection to the driver; the driver itself stays installed.
        _computer.Close();
    }

    private static HardwareCategory? CategoryOf(HardwareType type)
    {
        return type switch
        {
            HardwareType.Cpu => HardwareCategory.Cpu,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HardwareCategory.Gpu,
            HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => HardwareCategory.Mainboard,
            HardwareType.Storage => HardwareCategory.Storage,
            _ => null,
        };
    }

    private static HardwareSensorKind? KindOf(SensorType type)
    {
        return type switch
        {
            SensorType.Temperature => HardwareSensorKind.Temperature,
            SensorType.Fan => HardwareSensorKind.Fan,
            SensorType.Power => HardwareSensorKind.Power,
            _ => null,
        };
    }

    private static void Collect(IHardware hardware, HardwareCategory? inherited, List<HardwareSensorReading> readings)
    {
        HardwareCategory? category = CategoryOf(hardware.HardwareType) ?? inherited;
        if (category is null)
        {
            return;
        }

        hardware.Update();
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (KindOf(sensor.SensorType) is { } kind)
            {
                readings.Add(new HardwareSensorReading(
                    sensor.Identifier.ToString(),
                    category.Value,
                    hardware.Name,
                    sensor.Name,
                    kind,
                    sensor.Value));
            }
        }

        foreach (IHardware sub in hardware.SubHardware)
        {
            Collect(sub, category, readings);
        }
    }
}
