using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Link;

/// <summary>
/// System.Text.Json source-generation context for QoffeeLink: snake_case member names,
/// unknown members ignored, optional members omitted when null. Mandatory members and
/// non-nullable values are enforced when reading. No reflection-based serialization is
/// used (PLAN.md S14).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters = new[] { typeof(LinkMessageConverter) })]
[JsonSerializable(typeof(LinkMessage))]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(ClientHelloMessage))]
[JsonSerializable(typeof(GetManifestMessage))]
[JsonSerializable(typeof(ManifestChangedMessage))]
[JsonSerializable(typeof(SubscribeMessage))]
[JsonSerializable(typeof(StateMessage))]
[JsonSerializable(typeof(RunMessage))]
[JsonSerializable(typeof(EventMessage))]
[JsonSerializable(typeof(ByeMessage))]
[JsonSerializable(typeof(ResultMessage))]
[JsonSerializable(typeof(RunResultData))]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(PairRequest))]
[JsonSerializable(typeof(PairResponse))]
public sealed partial class LinkJsonContext : JsonSerializerContext
{
}
