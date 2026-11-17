using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Updates;

/// <summary>
/// System.Text.Json source-generation context for the answer of the GitHub releases API (T704): <c>snake_case</c> member names, unknown
/// members ignored, mandatory members enforced. It is only used for reading. No reflection-based serialization is used (PLAN.md S14).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(GitHubRelease))]
public sealed partial class UpdateJsonContext : JsonSerializerContext
{
}
