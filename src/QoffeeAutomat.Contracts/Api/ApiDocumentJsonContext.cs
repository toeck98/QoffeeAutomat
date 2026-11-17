using System.Text.Json.Serialization;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Contracts.Api;

/// <summary>
/// System.Text.Json source-generation context for the management API types that carry configuration
/// documents (commands, sensors, metric overrides, settings). It has the options of
/// <see cref="ConfigJsonContext"/> (camelCase, polymorphic actions and sensor sources, UTC timestamps) but
/// writes compact JSON. Missing members are not an error while reading: they read as default values and
/// <c>DocumentValidator</c> reports them with a JSON pointer, which the API answers with status 422. A JSON
/// value of the wrong kind still throws a <see cref="System.Text.Json.JsonException"/> (status 400). No
/// reflection is used (PLAN.md S14).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    Converters = new[] { typeof(CommandActionConverter), typeof(SensorSourceConverter), typeof(UtcTimestampConverter) })]
[JsonSerializable(typeof(CommandDocument))]
[JsonSerializable(typeof(SensorDocument))]
[JsonSerializable(typeof(MetricOverridesDocument))]
[JsonSerializable(typeof(AgentSettingsDocument))]
[JsonSerializable(typeof(ModeGroupDocument))]
[JsonSerializable(typeof(ModeGroupList))]
[JsonSerializable(typeof(MetricOverride))]
[JsonSerializable(typeof(CommandList))]
[JsonSerializable(typeof(SensorList))]
[JsonSerializable(typeof(CommandTestRequest))]
[JsonSerializable(typeof(TemplateList))]
[JsonSerializable(typeof(TemplateDetail))]
[JsonSerializable(typeof(TemplateInstantiation))]
public sealed partial class ApiDocumentJsonContext : JsonSerializerContext
{
}
