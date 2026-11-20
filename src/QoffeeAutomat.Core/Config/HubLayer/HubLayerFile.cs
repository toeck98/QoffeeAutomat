using System.Text.Json.Serialization;

namespace QoffeeAutomat.Core.Config.HubLayer;

/// <summary>An item in <c>config/hub/state.json</c>.</summary>
/// <param name="Kind">The wire kind.</param>
/// <param name="Id">The document id.</param>
/// <param name="Hash">The signed hash.</param>
/// <param name="State">The state of the item (<c>active</c> or <c>skipped_unsupported</c>).</param>
internal sealed record HubStateFileItem(string Kind, string Id, string Hash, string State);

/// <summary>
/// <c>config/hub/state.json</c>: the one file that decides which generation is active. A generation is stored completely in its own folder first; writing
/// this file (atomically) is the commit, so after a crash either the old or the new generation is active, never a mixture (PLAN.md S20, T908).
/// </summary>
/// <param name="Generation">The active generation; its documents are in the folder of that number.</param>
/// <param name="AppliedAt">When it was applied.</param>
/// <param name="LocalConfig">The policy <c>local_config</c>.</param>
/// <param name="DirectLink">The policy <c>direct_link</c>.</param>
/// <param name="Items">The items of the generation.</param>
internal sealed record HubStateFile(
    long Generation,
    DateTimeOffset AppliedAt,
    string LocalConfig,
    string DirectLink,
    IReadOnlyList<HubStateFileItem> Items);

/// <summary>Source-generated serialization of <see cref="HubStateFile"/>; no reflection is used.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(HubStateFile))]
internal sealed partial class HubLayerJsonContext : JsonSerializerContext
{
}
