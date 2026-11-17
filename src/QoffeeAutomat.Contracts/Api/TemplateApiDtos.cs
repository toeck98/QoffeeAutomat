using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Contracts.Api;

/// <summary>The values of <see cref="TemplateSummary.Kind"/> and <see cref="TemplateCreatedItem.Kind"/>.</summary>
public static class TemplateKinds
{
    /// <summary>The template is a command (<c>templates/commands/&lt;id&gt;.json</c>).</summary>
    public const string Command = "command";

    /// <summary>The template is a custom sensor (<c>templates/sensors/&lt;id&gt;.json</c>).</summary>
    public const string Sensor = "sensor";

    /// <summary>The template is a mode group (<c>templates/modes/&lt;id&gt;.json</c>, T717).</summary>
    public const string Mode = "mode";

    /// <summary>A script file that was copied to the scripts directory (only in <see cref="TemplateCreatedItem"/>).</summary>
    public const string Script = "script";
}

/// <summary>A template in the gallery listing (<c>GET /api/v1/templates</c>, PLAN.md S08, T701).</summary>
/// <param name="Id">The template id; it is also the id of the command or sensor that the first instance gets.</param>
/// <param name="Kind"><see cref="TemplateKinds.Command"/>, <see cref="TemplateKinds.Sensor"/> or <see cref="TemplateKinds.Mode"/>.</param>
/// <param name="Name">The display name.</param>
/// <param name="Description">What the template does.</param>
/// <param name="Icon">The icon.</param>
/// <param name="ActionType">The action type of a command (<c>powershell</c>, <c>process</c>, <c>builtin</c>, <c>sequence</c>), the source type of a sensor, or <c>mode</c> for a mode group.</param>
/// <param name="Requires">The ids of other command templates that an instance needs and that are created with it when they are missing.</param>
public sealed record TemplateSummary(
    string Id,
    string Kind,
    string Name,
    string? Description = null,
    string? Icon = null,
    string? ActionType = null,
    IReadOnlyList<string>? Requires = null);

/// <summary>Response of <c>GET /api/v1/templates</c>.</summary>
/// <param name="Templates">The templates, commands first, then sensors, then mode groups, each group ordered by id.</param>
public sealed record TemplateList(IReadOnlyList<TemplateSummary> Templates);

/// <summary>A script that belongs to a template.</summary>
/// <param name="Name">The file name in the scripts directory.</param>
/// <param name="Content">The text of the script.</param>
public sealed record TemplateScript(string Name, string Content);

/// <summary>A template with its document and scripts, for the preview (<c>GET /api/v1/templates/{id}</c>).</summary>
/// <param name="Summary">The listing entry.</param>
/// <param name="Command">The command document of a command template; absent for another kind.</param>
/// <param name="Sensor">The sensor document of a sensor template; absent for another kind.</param>
/// <param name="Scripts">The scripts that are copied when the template is added.</param>
/// <param name="Mode">The mode group document of a mode template; absent otherwise.</param>
public sealed record TemplateDetail(
    TemplateSummary Summary,
    CommandDocument? Command,
    SensorDocument? Sensor,
    IReadOnlyList<TemplateScript> Scripts,
    ModeGroupDocument? Mode = null);

/// <summary>One thing that adding a template created.</summary>
/// <param name="Kind">See <see cref="TemplateKinds"/>.</param>
/// <param name="Id">The id of the command, sensor or mode group, or the file name of the script.</param>
public sealed record TemplateCreatedItem(string Kind, string Id);

/// <summary>Response of <c>POST /api/v1/templates/{id}/instantiate</c> (201).</summary>
/// <param name="Kind"><see cref="TemplateKinds.Command"/>, <see cref="TemplateKinds.Sensor"/> or <see cref="TemplateKinds.Mode"/>.</param>
/// <param name="Id">The id of the new command, sensor or mode group; it differs from the template id when that id was taken.</param>
/// <param name="Created">Everything that was created, dependencies and scripts included, in the order it was written.</param>
public sealed record TemplateInstantiation(string Kind, string Id, IReadOnlyList<TemplateCreatedItem> Created);
