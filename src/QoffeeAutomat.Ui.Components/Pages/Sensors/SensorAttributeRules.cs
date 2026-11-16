using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Components.Pages.Sensors;

/// <summary>
/// What a custom sensor may tell Home Assistant (PLAN.md S06: the attributes are passed through and must be valid Home Assistant
/// constants), and which sensor types this platform can sample. The editor offers only the values listed here, so that a sensor
/// is not refused (or, worse, rejected by Home Assistant) because of a misspelt unit or a class that does not fit the value type.
/// </summary>
public static class SensorAttributeRules
{
    /// <summary>The value types of a sensor, in the order the editor offers them.</summary>
    public static readonly IReadOnlyList<string> ValueTypes = ["number", "string", "bool", "timestamp", "enum"];

    /// <summary>The units of a number sensor.</summary>
    public static readonly IReadOnlyList<string> Units =
    [
        "%", "°C", "°F", "K", "W", "kW", "Wh", "kWh", "V", "mV", "A", "mA", "Hz", "B", "kB", "MB", "GB", "TB",
        "bit/s", "kbit/s", "Mbit/s", "ms", "s", "min", "h", "d", "dBm", "lx", "hPa", "km/h",
    ];

    /// <summary>The device classes of a number sensor.</summary>
    public static readonly IReadOnlyList<string> NumberDeviceClasses =
    [
        "battery", "current", "data_rate", "data_size", "duration", "energy", "frequency", "humidity", "illuminance",
        "power", "pressure", "signal_strength", "speed", "temperature", "voltage",
    ];

    /// <summary>The device classes of a binary sensor.</summary>
    public static readonly IReadOnlyList<string> BinaryDeviceClasses =
    [
        "battery", "connectivity", "occupancy", "plug", "power", "presence", "problem", "running", "safety",
    ];

    /// <summary>The state classes of a number sensor.</summary>
    public static readonly IReadOnlyList<string> StateClasses = ["measurement", "total", "total_increasing"];

    /// <summary>The entity categories that a sensor may have.</summary>
    public static readonly IReadOnlyList<string> EntityCategories = ["diagnostic"];

    private static readonly string[] NeutralSourceTypes =
    [
        SensorSourceTypes.PowerShell,
        SensorSourceTypes.ProcessRunning,
    ];

    private static readonly string[] WindowsSourceTypes =
    [
        SensorSourceTypes.PerfCounter,
        SensorSourceTypes.CimQuery,
        SensorSourceTypes.ServiceState,
    ];

    /// <summary>
    /// The source types that the agent of a platform can sample and that the editor can author. Performance counters, CIM queries
    /// and service states exist on Windows only. The <c>state</c> type is not offered: its value is set from outside, which this
    /// version has no way to do.
    /// </summary>
    /// <param name="platform">The <c>platform.kind</c> of the agent status: <c>windows</c>, <c>linux</c> or <c>simulated</c>; <c>null</c> when it is not known.</param>
    /// <returns>The source types, in the order the editor offers them.</returns>
    public static IReadOnlyList<string> OfferedSourceTypes(string? platform)
    {
        return string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase)
            ? [.. NeutralSourceTypes, .. WindowsSourceTypes]
            : NeutralSourceTypes;
    }

    /// <summary>Tells whether the editor offers a source type on a platform.</summary>
    /// <param name="platform">The platform kind.</param>
    /// <param name="sourceType">The <c>source.type</c> of a sensor.</param>
    /// <returns>True when it is offered, so that a sensor of this type can be edited here.</returns>
    public static bool IsOffered(string? platform, string sourceType)
    {
        return OfferedSourceTypes(platform).Contains(sourceType, StringComparer.Ordinal);
    }

    /// <summary>The kind that goes with a value type: only a bool value makes a binary sensor.</summary>
    /// <param name="valueType">The value type.</param>
    /// <returns><c>binary_sensor</c> or <c>sensor</c>.</returns>
    public static string KindFor(string valueType)
    {
        return string.Equals(valueType, "bool", StringComparison.Ordinal) ? "binary_sensor" : "sensor";
    }

    /// <summary>Tells whether a unit applies to a value type.</summary>
    /// <param name="valueType">The value type.</param>
    /// <returns>True for numbers.</returns>
    public static bool AllowsUnit(string valueType) => string.Equals(valueType, "number", StringComparison.Ordinal);

    /// <summary>Tells whether a state class applies to a value type.</summary>
    /// <param name="valueType">The value type.</param>
    /// <returns>True for numbers.</returns>
    public static bool AllowsStateClass(string valueType) => string.Equals(valueType, "number", StringComparison.Ordinal);

    /// <summary>Tells whether a precision applies to a value type.</summary>
    /// <param name="valueType">The value type.</param>
    /// <returns>True for numbers.</returns>
    public static bool AllowsPrecision(string valueType) => string.Equals(valueType, "number", StringComparison.Ordinal);

    /// <summary>Tells whether a value type has a list of options.</summary>
    /// <param name="valueType">The value type.</param>
    /// <returns>True for enums.</returns>
    public static bool UsesOptions(string valueType) => string.Equals(valueType, "enum", StringComparison.Ordinal);

    /// <summary>The device classes that fit a value type; texts, timestamps and enums have none to choose.</summary>
    /// <param name="valueType">The value type.</param>
    /// <returns>The device classes; empty when there is no choice.</returns>
    public static IReadOnlyList<string> DeviceClassesFor(string valueType)
    {
        return valueType switch
        {
            "number" => NumberDeviceClasses,
            "bool" => BinaryDeviceClasses,
            _ => [],
        };
    }
}
