using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Session;

/// <summary>
/// System.Text.Json source-generation context for the session pipe: snake_case member names, unknown members
/// ignored, optional members omitted when null, mandatory members enforced when reading. No reflection-based
/// serialization is used (PLAN.md S14).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SessionMessage))]
[JsonSerializable(typeof(SessionHelloMessage))]
[JsonSerializable(typeof(SessionRunMessage))]
[JsonSerializable(typeof(SessionRunOutputMessage))]
[JsonSerializable(typeof(SessionRunResultMessage))]
[JsonSerializable(typeof(SessionCancelMessage))]
[JsonSerializable(typeof(SessionMetricsSubscribeMessage))]
[JsonSerializable(typeof(SessionMetricsStateMessage))]
[JsonSerializable(typeof(SessionNotifyMessage))]
[JsonSerializable(typeof(SessionEventMessage))]
[JsonSerializable(typeof(SessionShutdownMessage))]
[JsonSerializable(typeof(SessionTrayStateMessage))]
[JsonSerializable(typeof(SessionSetPauseMessage))]
public sealed partial class SessionJsonContext : JsonSerializerContext
{
}
