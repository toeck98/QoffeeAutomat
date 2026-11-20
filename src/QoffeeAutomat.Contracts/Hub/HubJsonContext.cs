using System.Text.Json.Serialization;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Hub;

/// <summary>
/// System.Text.Json source-generation context for the hub extensions of QoffeeLink and the enrollment documents:
/// snake_case member names, unknown members ignored, optional members omitted when null. Mandatory members and
/// non-nullable values are enforced when reading. No reflection-based serialization is used (PLAN.md S14).
/// Frames are read and written with the type-specific metadata of this context (for example
/// <c>HubJsonContext.Default.ConfigDesiredMessage</c>); the message type is chosen by the caller from the envelope field <c>t</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ConfigDesiredMessage))]
[JsonSerializable(typeof(ConfigDesiredResultData))]
[JsonSerializable(typeof(ConfigDocumentsMessage))]
[JsonSerializable(typeof(ConfigStatusMessage))]
[JsonSerializable(typeof(DesiredState))]
[JsonSerializable(typeof(MgmtRequestMessage))]
[JsonSerializable(typeof(MgmtRequestResultData))]
[JsonSerializable(typeof(MgmtStreamOpenMessage))]
[JsonSerializable(typeof(MgmtStreamOpenResultData))]
[JsonSerializable(typeof(MgmtStreamDataMessage))]
[JsonSerializable(typeof(MgmtStreamCloseMessage))]
[JsonSerializable(typeof(CredentialRotateMessage))]
[JsonSerializable(typeof(UnenrollMessage))]
[JsonSerializable(typeof(FleetHelloMessage))]
[JsonSerializable(typeof(GetAgentsMessage))]
[JsonSerializable(typeof(GetAgentsResultData))]
[JsonSerializable(typeof(AgentAddedMessage))]
[JsonSerializable(typeof(AgentRemovedMessage))]
[JsonSerializable(typeof(AgentStatusMessage))]
[JsonSerializable(typeof(FleetGetManifestMessage))]
[JsonSerializable(typeof(FleetSubscribeMessage))]
[JsonSerializable(typeof(FleetRunMessage))]
[JsonSerializable(typeof(FleetManifestChangedMessage))]
[JsonSerializable(typeof(FleetStateMessage))]
[JsonSerializable(typeof(FleetEventMessage))]
[JsonSerializable(typeof(EnrollmentPayload))]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(FleetPairResponse))]
[JsonSerializable(typeof(AgentInfo))]
public sealed partial class HubJsonContext : JsonSerializerContext
{
}
