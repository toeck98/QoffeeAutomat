using System.Text.Json;

namespace QoffeeAutomat.Contracts.Link;

/// <summary>
/// What the agent offers: metrics, commands and events (PLAN.md S06, S07). It is the data of
/// the result of <c>get_manifest</c>.
/// </summary>
/// <param name="Rev">First 12 hex characters of the SHA-256 of the canonical manifest without <c>rev</c>, see <see cref="ManifestRevision"/>.</param>
/// <param name="Agent">The agent.</param>
/// <param name="Metrics">The metrics.</param>
/// <param name="Commands">The commands.</param>
/// <param name="Events">Optional event types.</param>
/// <param name="Features">Optional features of the agent.</param>
public sealed record Manifest(
    string Rev,
    AgentInfo Agent,
    IReadOnlyList<MetricDescriptor> Metrics,
    IReadOnlyList<CommandDescriptor> Commands,
    IReadOnlyList<EventDescriptor>? Events = null,
    IReadOnlyList<string>? Features = null);

/// <summary>A metric announced in the manifest.</summary>
public sealed record MetricDescriptor(
    string Id,
    string Name,
    string Kind,
    string ValueType,
    string? TranslationKey = null,
    string? Unit = null,
    string? DeviceClass = null,
    string? StateClass = null,
    string? EntityCategory = null,
    string? Icon = null,
    int? Precision = null,
    IReadOnlyList<string>? Options = null,
    bool? EnabledDefault = null,
    double? IntervalSeconds = null,
    double? Deadband = null,
    string? Context = null);

/// <summary>A variable of a command, as announced in the manifest.</summary>
public sealed record VariableDescriptor(
    string Name,
    string Type,
    bool? Required = null,
    JsonElement? Default = null,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    string? Pattern = null,
    int? MaxLength = null,
    IReadOnlyList<string>? Options = null,
    bool? Secret = null,
    string? Unit = null,
    string? Description = null);

/// <summary>The Home Assistant control of a command (<c>haControl</c> in PLAN.md S06).</summary>
public sealed record ControlDescriptor(string Type, string? Var = null, string? StateMetric = null);

/// <summary>A command announced in the manifest.</summary>
public sealed record CommandDescriptor(
    string Id,
    string Name,
    string? Icon = null,
    string? Description = null,
    bool? ReturnsOutput = null,
    IReadOnlyList<VariableDescriptor>? Vars = null,
    ControlDescriptor? Control = null);

/// <summary>An event type that the agent can raise.</summary>
public sealed record EventDescriptor(string Type, string Name);
