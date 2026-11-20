using System.Text.Json.Serialization;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Fleet;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;

namespace QoffeeAutomat.Hub.Api;

/// <summary>
/// System.Text.Json source-generation context of the hub API (<c>/api/v1</c>): camelCase member names (PLAN.md S14), unknown members
/// ignored, optional members omitted when null. Like the agent API it does not insist on mandatory members when reading, so that an endpoint
/// can answer a missing one with a clear message. No reflection is used. The error body is the <see cref="ApiProblem"/> of the contracts.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = false)]
[JsonSerializable(typeof(SetupState))]
[JsonSerializable(typeof(HubSetupRequest))]
[JsonSerializable(typeof(HubLoginRequest))]
[JsonSerializable(typeof(HubSessionInfo))]
[JsonSerializable(typeof(UserInfo))]
[JsonSerializable(typeof(UserList))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(UpdateUserRequest))]
[JsonSerializable(typeof(SetPasswordRequest))]
[JsonSerializable(typeof(ChangeOwnPasswordRequest))]
[JsonSerializable(typeof(TokenInfo))]
[JsonSerializable(typeof(TokenList))]
[JsonSerializable(typeof(CreateTokenRequest))]
[JsonSerializable(typeof(CreatedToken))]
[JsonSerializable(typeof(AuditEntryInfo))]
[JsonSerializable(typeof(AuditPage))]
[JsonSerializable(typeof(HubInfo))]
[JsonSerializable(typeof(EnrollmentTokenSummary))]
[JsonSerializable(typeof(EnrollmentTokenList))]
[JsonSerializable(typeof(CreateEnrollmentTokenRequest))]
[JsonSerializable(typeof(CreatedEnrollmentToken))]
[JsonSerializable(typeof(AgentSummary))]
[JsonSerializable(typeof(AgentList))]
[JsonSerializable(typeof(UpdateAgentRequest))]
[JsonSerializable(typeof(CreateFleetPairingRequest))]
[JsonSerializable(typeof(FleetPairingInfo))]
[JsonSerializable(typeof(FleetTokenSummary))]
[JsonSerializable(typeof(FleetTokenList))]
[JsonSerializable(typeof(FleetScopeSummary))]
[JsonSerializable(typeof(StartBulkRunRequest))]
[JsonSerializable(typeof(StartFleetUpdateRequest))]
[JsonSerializable(typeof(FleetActionInfo))]
[JsonSerializable(typeof(FleetActionList))]
[JsonSerializable(typeof(FleetVersionOverview))]
[JsonSerializable(typeof(AgentPresenceInfo))]
[JsonSerializable(typeof(InventoryAgent))]
[JsonSerializable(typeof(InventoryList))]
[JsonSerializable(typeof(AgentEventInfo))]
[JsonSerializable(typeof(AgentLiveState))]
[JsonSerializable(typeof(InventoryDetail))]
[JsonSerializable(typeof(RunCommandRequest))]
[JsonSerializable(typeof(RunCommandResult))]
[JsonSerializable(typeof(LibraryDocumentList))]
[JsonSerializable(typeof(LibraryDocumentInfo))]
[JsonSerializable(typeof(SaveLibraryDocumentRequest))]
[JsonSerializable(typeof(LibraryRevisionList))]
[JsonSerializable(typeof(ProfileList))]
[JsonSerializable(typeof(ProfileInfo))]
[JsonSerializable(typeof(ProfileDraftRequest))]
[JsonSerializable(typeof(ProfileRevisionList))]
[JsonSerializable(typeof(ProfileRevisionInfo))]
[JsonSerializable(typeof(PublishPreview))]
[JsonSerializable(typeof(PublishResult))]
[JsonSerializable(typeof(PublishProfileRequest))]
[JsonSerializable(typeof(RollbackProfileRequest))]
[JsonSerializable(typeof(AssignmentList))]
[JsonSerializable(typeof(CreatedAssignment))]
[JsonSerializable(typeof(CreateAssignmentRequest))]
[JsonSerializable(typeof(AgentDesiredInfo))]
[JsonSerializable(typeof(AgentConfigStatusInfo))]
[JsonSerializable(typeof(RolloutOverview))]
[JsonSerializable(typeof(ProfileRolloutInfo))]
[JsonSerializable(typeof(AgentMetricList))]
[JsonSerializable(typeof(MetricQueryResult))]
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(ApiValidationError))]
public sealed partial class HubApiJsonContext : JsonSerializerContext
{
}
