using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Api;

/// <summary>
/// System.Text.Json source-generation context of the management API (<c>/api/v1</c>): camelCase member
/// names (PLAN.md S14), unknown members ignored, optional members omitted when null. Mandatory
/// members and non-nullable values are enforced when reading. No reflection is used. The configuration
/// documents themselves are read and written with <see cref="ApiDocumentJsonContext"/>, which does not
/// insist on mandatory members, so that the validator can report them with a JSON pointer.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = false)]
[JsonSerializable(typeof(SetupState))]
[JsonSerializable(typeof(SetupRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(AuthMeResponse))]
[JsonSerializable(typeof(PairingInfo))]
[JsonSerializable(typeof(LinkClientInfo))]
[JsonSerializable(typeof(LinkClientList))]
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(ApiValidationError))]
[JsonSerializable(typeof(ValidationReport))]
[JsonSerializable(typeof(CommandPatch))]
[JsonSerializable(typeof(ScriptSummary))]
[JsonSerializable(typeof(ScriptSummaryList))]
[JsonSerializable(typeof(ScriptContent))]
[JsonSerializable(typeof(ScriptWriteRequest))]
[JsonSerializable(typeof(MetricInfo))]
[JsonSerializable(typeof(MetricList))]
[JsonSerializable(typeof(SensorTestResult))]
[JsonSerializable(typeof(AgentStatus))]
[JsonSerializable(typeof(RunStartRequest))]
[JsonSerializable(typeof(RunStarted))]
[JsonSerializable(typeof(RunSummary))]
[JsonSerializable(typeof(RunDetail))]
[JsonSerializable(typeof(RunList))]
[JsonSerializable(typeof(RunOutputEvent))]
[JsonSerializable(typeof(RunEndEvent))]
[JsonSerializable(typeof(MetricStreamUpdate))]
[JsonSerializable(typeof(MetricHistoryResponse))]
[JsonSerializable(typeof(EventList))]
[JsonSerializable(typeof(LogList))]
[JsonSerializable(typeof(ConfigExportInfo))]
[JsonSerializable(typeof(ConfigImportResult))]
[JsonSerializable(typeof(DiagnosticsInfo))]
[JsonSerializable(typeof(ImportPreviewItem))]
[JsonSerializable(typeof(ConfigImportPreview))]
[JsonSerializable(typeof(RevisionEntry))]
[JsonSerializable(typeof(RevisionList))]
public sealed partial class ApiJsonContext : JsonSerializerContext
{
}
