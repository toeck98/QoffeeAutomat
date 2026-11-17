namespace QoffeeAutomat.Platform.Windows.Metrics.HardwareSensors;

/// <summary>What a hardware sensor measures. Only these three kinds are exposed (PLAN.md D-023, T707).</summary>
public enum HardwareSensorKind
{
    /// <summary>A temperature in degrees Celsius.</summary>
    Temperature,

    /// <summary>A fan speed in revolutions per minute.</summary>
    Fan,

    /// <summary>A power draw in watts.</summary>
    Power,
}

/// <summary>The part of the computer that a sensor sits in.</summary>
public enum HardwareCategory
{
    /// <summary>The processor.</summary>
    Cpu,

    /// <summary>A graphics card.</summary>
    Gpu,

    /// <summary>The mainboard, its embedded controller and its Super I/O chip.</summary>
    Mainboard,

    /// <summary>A drive.</summary>
    Storage,
}

/// <summary>One reading of one hardware sensor.</summary>
/// <param name="Path">
/// The stable identifier of the sensor as the library reports it, for example <c>/intelcpu/0/temperature/0</c>. The metric id
/// is derived from it, so it must not change between runs for the same sensor.
/// </param>
/// <param name="Category">Where the sensor sits.</param>
/// <param name="HardwareName">The name of the part, for example <c>Intel Core i7-12700K</c>.</param>
/// <param name="SensorName">The name of the sensor, for example <c>CPU Package</c>.</param>
/// <param name="Kind">What it measures.</param>
/// <param name="Value">The value in degrees Celsius, rpm or watts; null when the library has no value.</param>
public sealed record HardwareSensorReading(
    string Path,
    HardwareCategory Category,
    string HardwareName,
    string SensorName,
    HardwareSensorKind Kind,
    double? Value);

/// <summary>
/// An open connection to the hardware. Creating one is what loads the hardware library and lets it use the kernel driver, so it
/// exists only while the setting is on; <see cref="IDisposable.Dispose"/> closes the library and releases the driver handle.
/// </summary>
public interface IHardwareSource : IDisposable
{
    /// <summary>Updates the hardware and lists every temperature, fan and power sensor that it knows.</summary>
    /// <returns>The readings; sorted by path.</returns>
    IReadOnlyList<HardwareSensorReading> Read();
}

/// <summary>
/// Opens an <see cref="IHardwareSource"/>. This is the only seam that touches the hardware library: nothing may call it while the
/// setting is off (T707 acceptance), and the production implementation keeps all library types inside its own methods so that the
/// assembly is not even loaded before the first call.
/// </summary>
public interface IHardwareSourceFactory
{
    /// <summary>Loads the library, opens the hardware and, where it needs one, connects to the kernel driver.</summary>
    /// <returns>The open source; the caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">The library is too old or cannot be used.</exception>
    IHardwareSource Open();
}

/// <summary>Finds out whether the kernel driver that the library needs for processor and mainboard sensors is installed.</summary>
public interface IHardwareDriverProbe
{
    /// <summary>The name of the driver, for messages.</summary>
    string DriverName { get; }

    /// <summary>Looks whether the driver is installed. It never installs, starts or loads anything.</summary>
    /// <returns>True when the driver is installed.</returns>
    bool IsInstalled();
}
