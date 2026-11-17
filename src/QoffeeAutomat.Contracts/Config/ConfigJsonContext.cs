using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Config;

/// <summary>
/// System.Text.Json source-generation context for the configuration documents: camelCase
/// member names, indented output, unknown members ignored, optional members omitted when
/// null. Missing members are not an error while reading: they read as default values and
/// <c>DocumentValidator</c> reports them with a JSON pointer. A JSON value of the wrong kind
/// still throws a <see cref="System.Text.Json.JsonException"/>. No reflection-based
/// serialization is used (PLAN.md S14).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    WriteIndented = true,
    Converters = new[] { typeof(CommandActionConverter), typeof(SensorSourceConverter), typeof(UtcTimestampConverter) })]
[JsonSerializable(typeof(CommandDocument))]
[JsonSerializable(typeof(SensorDocument))]
[JsonSerializable(typeof(MetricOverridesDocument))]
[JsonSerializable(typeof(AgentSettingsDocument))]
[JsonSerializable(typeof(ModeGroupDocument))]
[JsonSerializable(typeof(ModeDefinition))]
[JsonSerializable(typeof(CommandAction))]
[JsonSerializable(typeof(PowerShellAction))]
[JsonSerializable(typeof(ProcessAction))]
[JsonSerializable(typeof(BuiltinAction))]
[JsonSerializable(typeof(SequenceAction))]
[JsonSerializable(typeof(SensorSource))]
[JsonSerializable(typeof(PowerShellSensorSource))]
[JsonSerializable(typeof(ProcessRunningSensorSource))]
[JsonSerializable(typeof(PerfCounterSensorSource))]
[JsonSerializable(typeof(CimQuerySensorSource))]
[JsonSerializable(typeof(ServiceStateSensorSource))]
[JsonSerializable(typeof(StateSensorSource))]
public sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
