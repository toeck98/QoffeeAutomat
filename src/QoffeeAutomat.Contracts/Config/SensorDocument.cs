using System.Text.Json;
using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Config;

/// <summary>
/// A custom sensor document, <c>config/sensors/&lt;id&gt;.json</c> (PLAN.md S06). The sensor
/// appears as the metric <c>custom.&lt;id&gt;</c>.
/// </summary>
public sealed record SensorDocument(
    int SchemaVersion,
    string Id,
    string Name,
    bool Enabled,
    bool ExposeToHa,
    string Kind,
    string ValueType,
    SensorSource Source,
    string? Description = null,
    string? Icon = null,
    bool? EnabledDefault = null,
    string? Unit = null,
    string? DeviceClass = null,
    string? StateClass = null,
    string? EntityCategory = null,
    int? Precision = null,
    IReadOnlyList<string>? Options = null,
    double? IntervalSeconds = null,
    double? Deadband = null,
    int? Revision = null,
    DateTimeOffset? UpdatedAt = null,
    string? Origin = null) : IConfigDocument;

/// <summary>The values of <c>source.type</c> of a custom sensor.</summary>
public static class SensorSourceTypes
{
    /// <summary>The last line of a PowerShell script's output.</summary>
    public const string PowerShell = "powershell";

    /// <summary>Whether a process is running.</summary>
    public const string ProcessRunning = "process_running";

    /// <summary>A Windows performance counter.</summary>
    public const string PerfCounter = "perf_counter";

    /// <summary>A CIM query (Windows).</summary>
    public const string CimQuery = "cim_query";

    /// <summary>The state of a service (Windows).</summary>
    public const string ServiceState = "service_state";

    /// <summary>A value that is set from outside.</summary>
    public const string State = "state";
}

/// <summary>
/// Where a custom sensor gets its value from. The concrete type follows <c>source.type</c>;
/// an unknown type becomes <see cref="UnknownSensorSource"/>, which the validator rejects.
/// </summary>
public abstract record SensorSource
{
    /// <summary>The value of <c>type</c>, see <see cref="SensorSourceTypes"/>.</summary>
    public abstract string Type { get; }
}

/// <summary>The last non-empty output line of a PowerShell script, parsed according to the value type.</summary>
/// <param name="Source">Inline code or a script file, see <see cref="PowerShellSources"/>.</param>
/// <param name="Code">The code when the source is inline.</param>
/// <param name="Script">The file name in the scripts directory when the source is a file.</param>
/// <param name="Engine">See <see cref="PowerShellEngines"/>.</param>
public sealed record PowerShellSensorSource(
    string Source,
    string? Code = null,
    string? Script = null,
    string Engine = PowerShellEngines.Auto) : SensorSource
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Type => SensorSourceTypes.PowerShell;
}

/// <summary>True while a process with the given name is running.</summary>
/// <param name="Process">The process name.</param>
public sealed record ProcessRunningSensorSource(string Process) : SensorSource
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Type => SensorSourceTypes.ProcessRunning;
}

/// <summary>A Windows performance counter.</summary>
/// <param name="Category">The counter category.</param>
/// <param name="Counter">The counter name.</param>
/// <param name="Instance">The optional instance.</param>
public sealed record PerfCounterSensorSource(string Category, string Counter, string? Instance = null) : SensorSource
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Type => SensorSourceTypes.PerfCounter;
}

/// <summary>A property of the first result of a CIM query.</summary>
/// <param name="Namespace">The CIM namespace.</param>
/// <param name="ClassName">The CIM class (the member is named <c>class</c> in JSON).</param>
/// <param name="Property">The property to read.</param>
/// <param name="Filter">An optional filter.</param>
public sealed record CimQuerySensorSource(
    string Namespace,
    [property: JsonPropertyName("class")] string ClassName,
    string Property,
    string? Filter = null) : SensorSource
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Type => SensorSourceTypes.CimQuery;
}

/// <summary>The state of a service.</summary>
/// <param name="Service">The service name.</param>
public sealed record ServiceStateSensorSource(string Service) : SensorSource
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Type => SensorSourceTypes.ServiceState;
}

/// <summary>A value that is not sampled but set from outside (after v1).</summary>
/// <param name="InitialValue">The value after a start.</param>
/// <param name="Persist">Whether the value is kept across restarts.</param>
public sealed record StateSensorSource(JsonElement? InitialValue = null, bool Persist = false) : SensorSource
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Type => SensorSourceTypes.State;
}

/// <summary>A sensor source with a type that this version does not know.</summary>
/// <param name="TypeName">The value of <c>type</c>; empty when the member is missing.</param>
/// <param name="Raw">The source as it was read.</param>
public sealed record UnknownSensorSource(string TypeName, JsonElement Raw) : SensorSource
{
    /// <inheritdoc />
    public override string Type => TypeName;
}
