using System.Text.Json.Serialization;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Fleet;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;

namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>
/// System.Text.Json source-generation context of the hub UI for the hub API (<c>/api/v1</c>): the same options as the hub's own context
/// (camelCase member names, unknown members ignored, null members omitted, PLAN.md S14), so what the hub writes is what the UI reads. No
/// reflection is used.
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
[JsonSerializable(typeof(ChangeOwnPasswordRequest))]
[JsonSerializable(typeof(HubInfo))]
[JsonSerializable(typeof(AgentList))]
[JsonSerializable(typeof(AgentSummary))]
[JsonSerializable(typeof(UpdateAgentRequest))]
[JsonSerializable(typeof(InventoryList))]
[JsonSerializable(typeof(InventoryDetail))]
[JsonSerializable(typeof(RolloutOverview))]
[JsonSerializable(typeof(AgentConfigStatusInfo))]
[JsonSerializable(typeof(EnrollmentTokenList))]
[JsonSerializable(typeof(CreateEnrollmentTokenRequest))]
[JsonSerializable(typeof(CreatedEnrollmentToken))]
[JsonSerializable(typeof(CreateFleetPairingRequest))]
[JsonSerializable(typeof(FleetPairingInfo))]
[JsonSerializable(typeof(FleetTokenList))]
[JsonSerializable(typeof(StartBulkRunRequest))]
[JsonSerializable(typeof(StartFleetUpdateRequest))]
[JsonSerializable(typeof(FleetActionInfo))]
[JsonSerializable(typeof(FleetActionList))]
[JsonSerializable(typeof(FleetVersionOverview))]
[JsonSerializable(typeof(UserList))]
[JsonSerializable(typeof(UserInfo))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(UpdateUserRequest))]
[JsonSerializable(typeof(SetPasswordRequest))]
[JsonSerializable(typeof(TokenList))]
[JsonSerializable(typeof(CreateTokenRequest))]
[JsonSerializable(typeof(CreatedToken))]
[JsonSerializable(typeof(AuditPage))]
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(LibraryDocumentList))]
[JsonSerializable(typeof(LibraryDocumentInfo))]
[JsonSerializable(typeof(SaveLibraryDocumentRequest))]
[JsonSerializable(typeof(LibraryRevisionList))]
[JsonSerializable(typeof(ProfileList))]
[JsonSerializable(typeof(ProfileInfo))]
[JsonSerializable(typeof(ProfileDraftRequest))]
[JsonSerializable(typeof(ProfileRevisionList))]
[JsonSerializable(typeof(ProfileRevisionInfo))]
[JsonSerializable(typeof(PublishProfileRequest))]
[JsonSerializable(typeof(RollbackProfileRequest))]
[JsonSerializable(typeof(PublishPreview))]
[JsonSerializable(typeof(PublishResult))]
[JsonSerializable(typeof(ProfileRolloutInfo))]
[JsonSerializable(typeof(AssignmentList))]
[JsonSerializable(typeof(CreateAssignmentRequest))]
[JsonSerializable(typeof(CreatedAssignment))]
[JsonSerializable(typeof(AgentDesiredInfo))]
[JsonSerializable(typeof(AgentMetricList))]
[JsonSerializable(typeof(MetricQueryResult))]
public sealed partial class HubUiJsonContext : JsonSerializerContext
{
}
