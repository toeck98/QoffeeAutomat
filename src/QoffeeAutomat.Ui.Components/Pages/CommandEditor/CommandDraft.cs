using System.Globalization;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Components.Pages.CommandEditor;

/// <summary>A draft turned into a document, with the problems that the editor found while doing so.</summary>
/// <param name="Document">The document that is validated, tested and saved.</param>
/// <param name="Problems">What could not be turned into a value, by JSON pointer into the document.</param>
public sealed record DraftBuild(CommandDocument Document, IReadOnlyList<ApiValidationError> Problems);

/// <summary>
/// A command while it is edited (PLAN.md T404): plain mutable fields that the form binds to, with text for everything
/// that is typed (numbers, lists), and <see cref="Build"/> to make the document of it. The id follows the name until the
/// user edits it, and cannot change once the command is saved.
/// </summary>
public sealed class CommandDraft
{
    /// <summary>The seconds of the timeout of a new command.</summary>
    public const int DefaultTimeoutSeconds = 60;

    private string _name = string.Empty;
    private string _id = string.Empty;

    /// <summary>True until the command was saved for the first time; only then the id can be edited.</summary>
    public bool IsNew { get; set; } = true;

    /// <summary>The schema version of the document.</summary>
    public int SchemaVersion { get; set; } = DocumentSchema.CurrentVersion;

    /// <summary>The id. Editing it stops the derivation from the name; emptying it starts it again.</summary>
    public string Id
    {
        get => _id;
        set
        {
            _id = value ?? string.Empty;
            IdEdited = _id.Length > 0;
            if (!IdEdited && IsNew)
            {
                // An emptied id starts following the name again.
                _id = CommandSlug.FromName(_name);
            }
        }
    }

    /// <summary>True when the user typed the id, so that the name no longer changes it.</summary>
    public bool IdEdited { get; private set; }

    /// <summary>The display name. While the id is not edited and the command is new, the id is made from it.</summary>
    public string Name
    {
        get => _name;
        set
        {
            _name = value ?? string.Empty;
            if (IsNew && !IdEdited)
            {
                _id = CommandSlug.FromName(value);
            }
        }
    }

    /// <summary>The description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The icon, for example <c>mdi:power</c>.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>Disabled commands are rejected everywhere.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Only enabled and exposed commands appear in Home Assistant.</summary>
    public bool ExposeToHa { get; set; } = true;

    /// <summary>One of <see cref="RunAsValues"/>.</summary>
    public string RunAs { get; set; } = RunAsValues.User;

    /// <summary>One of <see cref="ActionTypes"/>, or the type of an action that this version does not know.</summary>
    public string ActionType { get; set; } = ActionTypes.PowerShell;

    /// <summary>One of <see cref="PowerShellSources"/>.</summary>
    public string PowerShellSource { get; set; } = PowerShellSources.Inline;

    /// <summary>The inline code.</summary>
    public string PowerShellCode { get; set; } = string.Empty;

    /// <summary>The name of the script file.</summary>
    public string PowerShellScript { get; set; } = string.Empty;

    /// <summary>One of <see cref="PowerShellEngines"/>.</summary>
    public string PowerShellEngine { get; set; } = PowerShellEngines.Auto;

    /// <summary>The executable of a process action.</summary>
    public string ProcessFile { get; set; } = string.Empty;

    /// <summary>The arguments of a process action, one per line.</summary>
    public string ProcessArgsText { get; set; } = string.Empty;

    /// <summary>The working directory of a process action.</summary>
    public string ProcessWorkingDirectory { get; set; } = string.Empty;

    /// <summary>Whether the run waits for the process to end.</summary>
    public bool ProcessWaitForExit { get; set; } = true;

    /// <summary>The name of a built-in action.</summary>
    public string BuiltinName { get; set; } = string.Empty;

    /// <summary>The steps of a sequence.</summary>
    public List<StepDraft> Steps { get; } = [];

    /// <summary>One of <see cref="SequenceErrorModes"/>.</summary>
    public string SequenceOnError { get; set; } = SequenceErrorModes.Stop;

    /// <summary>The rows of the parameter table.</summary>
    public List<ParameterDraft> Parameters { get; } = [];

    /// <summary>The type of the Home Assistant control, one of <see cref="HaControlTypes"/>.</summary>
    public string HaType { get; set; } = HaControlTypes.None;

    /// <summary>The parameter that the control sets.</summary>
    public string HaParameter { get; set; } = string.Empty;

    /// <summary>The metric that reports the real state.</summary>
    public string HaStateMetric { get; set; } = string.Empty;

    /// <summary>The timeout in seconds, as typed.</summary>
    public string TimeoutText { get; set; } = DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>One of <see cref="ConcurrencyModes"/>.</summary>
    public string Concurrency { get; set; } = ConcurrencyModes.Exclusive;

    /// <summary>The cooldown in seconds, as typed.</summary>
    public string CooldownText { get; set; } = "0";

    /// <summary>Whether stdout and stderr are captured.</summary>
    public bool CaptureOutput { get; set; }

    /// <summary>The revision of the stored command; the save is refused when somebody else saved in between.</summary>
    public int? Revision { get; set; }

    /// <summary>When the stored command was saved.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Where the stored command came from.</summary>
    public string? Origin { get; set; }

    /// <summary>True when the action is one of the four types that the editor has a form for.</summary>
    public bool HasKnownAction => ActionType is ActionTypes.PowerShell or ActionTypes.Process or ActionTypes.Builtin or ActionTypes.Sequence;

    /// <summary>Creates the draft of a stored command.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The draft; it is not new.</returns>
    public static CommandDraft From(CommandDocument command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var draft = new CommandDraft { IsNew = false };
        draft.Load(command);
        return draft;
    }

    /// <summary>Takes over the stored command after a save: the draft is the stored command from now on.</summary>
    /// <param name="saved">The command as the agent stored it.</param>
    public void Adopt(CommandDocument saved)
    {
        ArgumentNullException.ThrowIfNull(saved);

        IsNew = false;
        Revision = saved.Revision;
        UpdatedAt = saved.UpdatedAt;
        Origin = saved.Origin;
        SchemaVersion = saved.SchemaVersion;
    }

    /// <summary>Renames a parameter and keeps the Home Assistant control on it.</summary>
    /// <param name="parameter">The row.</param>
    /// <param name="name">The new name.</param>
    public void RenameParameter(ParameterDraft parameter, string name)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(name);

        if (parameter.Name.Length > 0 && string.Equals(HaParameter, parameter.Name, StringComparison.Ordinal))
        {
            HaParameter = name;
        }

        parameter.Name = name;
    }

    /// <summary>Removes a parameter; a control that used it has no parameter then.</summary>
    /// <param name="parameter">The row.</param>
    public void RemoveParameter(ParameterDraft parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        Parameters.Remove(parameter);
        if (parameter.Name.Length > 0 && string.Equals(HaParameter, parameter.Name, StringComparison.Ordinal))
        {
            HaParameter = string.Empty;
        }
    }

    /// <summary>A name for the next parameter that no row has.</summary>
    /// <returns>For example <c>Value</c>, <c>Value2</c>.</returns>
    public string NextParameterName()
    {
        const string stem = "Value";
        string name = stem;
        for (int number = 2; Parameters.Exists(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)); number++)
        {
            name = stem + number.ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    /// <summary>The parameters as the document will have them.</summary>
    /// <returns>The parameters in table order.</returns>
    public IReadOnlyList<ParameterDefinition> ParameterDefinitions()
    {
        var ignored = new List<ApiValidationError>();
        return [.. Parameters.Select((row, index) => row.ToDefinition("/parameters/" + index.ToString(CultureInfo.InvariantCulture), ignored))];
    }

    /// <summary>Makes the document of the draft.</summary>
    /// <returns>The document and the problems that were found on the way.</returns>
    public DraftBuild Build()
    {
        var problems = new List<ApiValidationError>();
        CommandAction action = BuildAction(problems);

        var parameters = new List<ParameterDefinition>(Parameters.Count);
        for (int i = 0; i < Parameters.Count; i++)
        {
            parameters.Add(Parameters[i].ToDefinition("/parameters/" + i.ToString(CultureInfo.InvariantCulture), problems));
        }

        HaControl? control = HaType == HaControlTypes.None
            ? null
            : new HaControl(HaType, ParameterDraft.Blank(HaParameter), ParameterDraft.Blank(HaStateMetric));

        int timeout = ParseSeconds(TimeoutText, "/timeoutSeconds", DefaultTimeoutSeconds, problems);
        int cooldown = ParseSeconds(CooldownText, "/cooldownSeconds", 0, problems);

        var document = new CommandDocument(
            SchemaVersion,
            Id.Trim(),
            Name.Trim(),
            Enabled,
            ExposeToHa,
            RunAs,
            action,
            ParameterDraft.Blank(Description),
            ParameterDraft.Blank(Icon),
            parameters.Count == 0 ? null : parameters,
            control,
            timeout,
            Concurrency,
            cooldown,
            CaptureOutput,
            Revision,
            UpdatedAt,
            Origin);
        return new DraftBuild(document, problems);
    }

    private static int ParseSeconds(string text, string path, int fallback, List<ApiValidationError> problems)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return fallback;
        }

        if (int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int seconds))
        {
            return seconds;
        }

        problems.Add(new ApiValidationError(path, ParameterDraft.NotANumberCode, "This is not a whole number."));
        return fallback;
    }

    private CommandAction BuildAction(List<ApiValidationError> problems)
    {
        switch (ActionType)
        {
            case ActionTypes.Process:
                return new ProcessAction(
                    ProcessFile.Trim(),
                    ParameterDraft.SplitLines(ProcessArgsText).Count == 0 ? null : ParameterDraft.SplitLines(ProcessArgsText),
                    ParameterDraft.Blank(ProcessWorkingDirectory),
                    ProcessWaitForExit);
            case ActionTypes.Builtin:
                return new BuiltinAction(BuiltinName.Trim());
            case ActionTypes.Sequence:
                var steps = new List<SequenceStep>(Steps.Count);
                for (int i = 0; i < Steps.Count; i++)
                {
                    steps.Add(Steps[i].ToStep("/action/steps/" + i.ToString(CultureInfo.InvariantCulture), problems));
                }

                return new SequenceAction(steps, SequenceOnError);
            default:
                bool inline = PowerShellSource == PowerShellSources.Inline;
                return new PowerShellAction(
                    PowerShellSource,
                    inline ? PowerShellCode : null,
                    inline ? null : ParameterDraft.Blank(PowerShellScript),
                    PowerShellEngine);
        }
    }

    private void Load(CommandDocument command)
    {
        SchemaVersion = command.SchemaVersion;
        _id = command.Id ?? string.Empty;
        IdEdited = _id.Length > 0;
        _name = command.Name ?? string.Empty;
        Description = command.Description ?? string.Empty;
        Icon = command.Icon ?? string.Empty;
        Enabled = command.Enabled;
        ExposeToHa = command.ExposeToHa;
        RunAs = command.RunAs ?? RunAsValues.User;
        Revision = command.Revision;
        UpdatedAt = command.UpdatedAt;
        Origin = command.Origin;
        TimeoutText = command.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        Concurrency = command.Concurrency ?? ConcurrencyModes.Exclusive;
        CooldownText = command.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
        CaptureOutput = command.CaptureOutput;

        foreach (ParameterDefinition parameter in command.Parameters ?? [])
        {
            Parameters.Add(ParameterDraft.From(parameter));
        }

        if (command.HaControl is { } control)
        {
            HaType = control.Type ?? HaControlTypes.None;
            HaParameter = control.Parameter ?? string.Empty;
            HaStateMetric = control.StateMetric ?? string.Empty;
        }

        ActionType = command.Action?.Type ?? ActionTypes.PowerShell;
        switch (command.Action)
        {
            case PowerShellAction powerShell:
                PowerShellSource = powerShell.Source ?? PowerShellSources.Inline;
                PowerShellCode = powerShell.Code ?? string.Empty;
                PowerShellScript = powerShell.Script ?? string.Empty;
                PowerShellEngine = powerShell.Engine ?? PowerShellEngines.Auto;
                break;
            case ProcessAction process:
                ProcessFile = process.File ?? string.Empty;
                ProcessArgsText = string.Join('\n', process.Args ?? []);
                ProcessWorkingDirectory = process.WorkingDirectory ?? string.Empty;
                ProcessWaitForExit = process.WaitForExit;
                break;
            case BuiltinAction builtin:
                BuiltinName = builtin.Name ?? string.Empty;
                break;
            case SequenceAction sequence:
                SequenceOnError = sequence.OnError ?? SequenceErrorModes.Stop;
                Steps.AddRange((sequence.Steps ?? []).Select(StepDraft.From));
                break;
        }
    }
}
