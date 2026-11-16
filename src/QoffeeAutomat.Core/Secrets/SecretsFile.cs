using System.Text.Json.Serialization;

namespace QoffeeAutomat.Core.Secrets;

/// <summary>The JSON model of <c>secrets/secrets.json</c>. The values of <see cref="Secrets"/> are base64 of protected blobs.</summary>
/// <param name="SchemaVersion">Version of the file format.</param>
/// <param name="Protector">The <see cref="ISecretProtector.Scheme"/> that wrote the file.</param>
/// <param name="Secrets">Secret name to protected blob.</param>
internal sealed record SecretsFile(int SchemaVersion, string Protector, IReadOnlyDictionary<string, string>? Secrets);

/// <summary>Source-generated serialization of <see cref="SecretsFile"/>; no reflection is used.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(SecretsFile))]
internal sealed partial class SecretsJsonContext : JsonSerializerContext
{
}
