using System.Text.Json;
using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Config;

/// <summary>
/// A command document, <c>config/commands/&lt;id&gt;.json</c> (PLAN.md S06). Members that the
/// schema requires have no default; a missing member is reported by <c>DocumentValidator</c>
/// (a missing boolean reads as false, which keeps a command disabled).
/// </summary>
public sealed record CommandDocument(
    int SchemaVersion,
    string Id,
    string Name,
    bool Enabled,
    bool ExposeToHa,
    string RunAs,
    CommandAction Action,
    string? Description = null,
    string? Icon = null,
    IReadOnlyList<ParameterDefinition>? Parameters = null,
    HaControl? HaControl = null,
    int TimeoutSeconds = 60,
    string Concurrency = ConcurrencyModes.Exclusive,
    int CooldownSeconds = 0,
    bool CaptureOutput = false,
    int? Revision = null,
    DateTimeOffset? UpdatedAt = null,
    string? Origin = null) : IConfigDocument;

/// <summary>A command variable (R-04, PLAN.md S06).</summary>
/// <param name="Name">Matches <c>^[A-Za-z][A-Za-z0-9_]{0,31}$</c>, unique per command ignoring case.</param>
/// <param name="Type">One of <see cref="ParameterTypes"/>.</param>
/// <param name="Required">A missing required value is the error <c>invalid_vars</c>.</param>
/// <param name="Default">The default value; its JSON type must fit <paramref name="Type"/>.</param>
/// <param name="Min">Lower bound (int and float).</param>
/// <param name="Max">Upper bound (int and float).</param>
/// <param name="Step">Step (int and float).</param>
/// <param name="Pattern">A .NET regular expression, matched with a 100 ms timeout (string).</param>
/// <param name="MaxLength">Maximum length, defaults to 1024 (string).</param>
/// <param name="Options">The allowed values (enum).</param>
/// <param name="OptionsFrom">An enum metric id whose options replace <paramref name="Options"/> (enum, after v1).</param>
/// <param name="Secret">The value is redacted in history, logs and audit entries.</param>
/// <param name="Unit">The unit shown in the UI and in Home Assistant.</param>
/// <param name="Description">The description shown in the UI and in Home Assistant.</param>
public sealed record ParameterDefinition(
    string Name,
    string Type,
    bool Required = false,
    JsonElement? Default = null,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    string? Pattern = null,
    int? MaxLength = null,
    IReadOnlyList<string>? Options = null,
    string? OptionsFrom = null,
    bool Secret = false,
    string? Unit = null,
    string? Description = null)
{
    /// <summary>The default maximum length of a string value.</summary>
    public const int DefaultMaxLength = 1024;
}

/// <summary>Maps a command to a Home Assistant entity (PLAN.md S06).</summary>
/// <param name="Type">One of <see cref="HaControlTypes"/>.</param>
/// <param name="Parameter">The parameter that the entity sets (switch, select, number, text).</param>
/// <param name="StateMetric">Optional metric that reports the real state; without it the entity is optimistic.</param>
public sealed record HaControl(string Type, string? Parameter = null, string? StateMetric = null);

/// <summary>
/// The action of a command. The concrete type follows <c>action.type</c>; an unknown type
/// becomes <see cref="UnknownAction"/>, which the validator rejects.
/// </summary>
public abstract record CommandAction
{
    /// <summary>The value of <c>type</c>, see <see cref="ActionTypes"/>.</summary>
    public abstract string Type { get; }
}

/// <summary>Runs PowerShell through the wrapper of D-012.</summary>
/// <param name="Source">Inline code or a script file, see <see cref="PowerShellSources"/>.</param>
/// <param name="Code">The code when the source is inline.</param>
/// <param name="Script">The file name in the scripts directory when the source is a file.</param>
/// <param name="Engine">See <see cref="PowerShellEngines"/>.</param>
public sealed record PowerShellAction(
    string Source,
    string? Code = null,
    string? Script = null,
    string Engine = PowerShellEngines.Auto) : CommandAction
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Type => ActionTypes.PowerShell;
}

/// <summary>Starts an executable without a shell.</summary>
/// <param name="File">The executable.</param>
/// <param name="Args">Arguments; <c>{{Name}}</c> placeholders are replaced by variable values.</param>
/// <param name="WorkingDirectory">The working directory.</param>
/// <param name="WaitForExit">Whether the run waits for the process to end.</param>
public sealed record ProcessAction(
    string File,
    IReadOnlyList<string>? Args = null,
    string? WorkingDirectory = null,
    bool WaitForExit = true) : CommandAction
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Type => ActionTypes.Process;
}

/// <summary>A platform-provided action such as <c>power.shutdown</c>.</summary>
/// <param name="Name">The action name.</param>
public sealed record BuiltinAction(string Name) : CommandAction
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Type => ActionTypes.Builtin;
}

/// <summary>Runs other commands in order.</summary>
/// <param name="Steps">The steps.</param>
/// <param name="OnError">See <see cref="SequenceErrorModes"/>.</param>
public sealed record SequenceAction(
    IReadOnlyList<SequenceStep> Steps,
    string OnError = SequenceErrorModes.Stop) : CommandAction
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Type => ActionTypes.Sequence;
}

/// <summary>A step of a sequence: run a command with variables, or wait.</summary>
/// <param name="Command">The id of the command to run.</param>
/// <param name="Vars">Variable values for the command.</param>
/// <param name="DelaySeconds">Seconds to wait instead of running a command.</param>
public sealed record SequenceStep(
    string? Command = null,
    IReadOnlyDictionary<string, JsonElement>? Vars = null,
    double? DelaySeconds = null);

/// <summary>An action with a type that this version does not know.</summary>
/// <param name="TypeName">The value of <c>type</c>; empty when the member is missing.</param>
/// <param name="Raw">The action as it was read.</param>
public sealed record UnknownAction(string TypeName, JsonElement Raw) : CommandAction
{
    /// <inheritdoc />
    public override string Type => TypeName;
}
