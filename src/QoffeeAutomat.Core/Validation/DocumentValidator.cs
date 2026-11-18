using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Commands.Conditions;
using QoffeeAutomat.Core.Secrets.Named;

namespace QoffeeAutomat.Core.Validation;

/// <summary>
/// Semantic validation of the configuration documents (PLAN.md S06): ids, names, the
/// type-specific parameter rules, the <c>haControl</c> requirements, actions and sequence
/// cycles. The structure itself is described by the JSON Schemas in <c>protocol/schema/config</c>.
/// All methods return structured errors with JSON pointers; none throws for invalid content.
/// </summary>
public static class DocumentValidator
{
    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 2000;
    private const int MaxTimeoutSeconds = 86400;
    private const int MaxPrometheusMetrics = 500;
    private const int MaxHotkeys = 32;
    private const int MaxModeGroupIdLength = 58;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex IdPattern = new(@"^[a-z][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex ParameterNamePattern = new(@"^[A-Za-z][A-Za-z0-9_]{0,31}\z", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex MetricIdPattern = new(@"^[a-z][a-z0-9_]*(\.[a-z0-9_-]+)*\z", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex IconPattern = new(@"^[a-z0-9_]+:[a-z0-9_-]+\z", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex BuiltinNamePattern = new(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+\z", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex ScriptNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex PlaceholderPattern = new(@"\{\{(?<name>[^{}]*)\}\}", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex PlaceholderNamePattern = new(@"^[A-Za-z][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly HashSet<string> RunAsSet = new(StringComparer.Ordinal)
    {
        RunAsValues.System,
        RunAsValues.User,
        RunAsValues.UserElevated,
    };

    private static readonly HashSet<string> ConcurrencySet = new(StringComparer.Ordinal)
    {
        ConcurrencyModes.Exclusive,
        ConcurrencyModes.Queue,
        ConcurrencyModes.Parallel,
        ConcurrencyModes.Restart,
    };

    private static readonly HashSet<string> SessionRequirementSet = new(StringComparer.Ordinal)
    {
        SessionRequirements.Any,
        SessionRequirements.LoggedIn,
        SessionRequirements.Unlocked,
        SessionRequirements.Locked,
    };

    private static readonly HashSet<string> ParameterTypeSet = new(StringComparer.Ordinal)
    {
        ParameterTypes.Text,
        ParameterTypes.WholeNumber,
        ParameterTypes.Real,
        ParameterTypes.Flag,
        ParameterTypes.Choice,
    };

    private static readonly HashSet<string> HaControlSet = new(StringComparer.Ordinal)
    {
        HaControlTypes.None,
        HaControlTypes.Button,
        HaControlTypes.Switch,
        HaControlTypes.Select,
        HaControlTypes.Number,
        HaControlTypes.Text,
    };

    private static readonly HashSet<string> OriginSet = new(StringComparer.Ordinal)
    {
        DocumentOrigins.Local,
        DocumentOrigins.Hub,
    };

    private static readonly HashSet<string> EngineSet = new(StringComparer.Ordinal)
    {
        PowerShellEngines.Auto,
        PowerShellEngines.Pwsh,
        PowerShellEngines.PowerShell,
    };

    private static readonly HashSet<string> ModeBootSet = new(StringComparer.Ordinal)
    {
        ModeBootBehaviors.Keep,
        ModeBootBehaviors.Reset,
        ModeBootBehaviors.ApplyDefault,
    };

    private static readonly HashSet<string> SensorKindSet = new(StringComparer.Ordinal) { "sensor", "binary_sensor" };

    private static readonly HashSet<string> SensorValueTypeSet = new(StringComparer.Ordinal)
    {
        "number",
        "string",
        "bool",
        "timestamp",
        "enum",
    };

    private static readonly HashSet<string> LogLevelSet = new(StringComparer.Ordinal)
    {
        "trace",
        "debug",
        "information",
        "warning",
        "error",
        "critical",
    };

    // Parameter types that each haControl type accepts (PLAN.md S06).
    private static readonly string[] SwitchParameterTypes = [ParameterTypes.Flag];
    private static readonly string[] SelectParameterTypes = [ParameterTypes.Choice];
    private static readonly string[] NumberParameterTypes = [ParameterTypes.WholeNumber, ParameterTypes.Real];
    private static readonly string[] TextParameterTypes = [ParameterTypes.Text];

    private static readonly HashSet<string> ContextVariableSet = new(StringComparer.Ordinal)
    {
        "qa.run_id",
        "qa.source",
        "qa.command_id",
        "qa.agent_name",
    };

    /// <summary>Validates a command document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="lookup">
    /// Optional lookup of other stored commands by id, used to detect cycles through sequences.
    /// The document being validated is never looked up; it is used as given.
    /// </param>
    /// <returns>The errors; empty when the document is valid.</returns>
    public static ValidationResult ValidateCommand(CommandDocument document, Func<string, CommandDocument?>? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<ValidationError>();

        CheckHeader(document, errors, DocumentSchema.CommandVersion);
        CheckId(document.Id, "/id", errors);
        if (SystemCommands.IsReservedId(document.Id))
        {
            Add(errors, "/id", ValidationCodes.Conflict, "This id is reserved for a command that the agent provides itself.");
        }

        CheckName(document.Name, "/name", errors);
        CheckDescriptionAndIcon(document.Description, document.Icon, errors);

        if (string.IsNullOrEmpty(document.RunAs))
        {
            Add(errors, "/runAs", ValidationCodes.Required, "runAs is required.");
        }
        else if (!RunAsSet.Contains(document.RunAs))
        {
            Add(errors, "/runAs", ValidationCodes.Invalid, "runAs must be system, user or user_elevated.");
        }

        if (document.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            Add(errors, "/timeoutSeconds", ValidationCodes.Invalid, "timeoutSeconds must be between 1 and 86400.");
        }

        if (!ConcurrencySet.Contains(document.Concurrency ?? string.Empty))
        {
            Add(errors, "/concurrency", ValidationCodes.Invalid, "concurrency must be single, queue, parallel or restart.");
        }

        if (document.CooldownSeconds < 0)
        {
            Add(errors, "/cooldownSeconds", ValidationCodes.Invalid, "cooldownSeconds must not be negative.");
        }

        CheckParameters(document.Parameters, errors);
        CheckHaControl(document.HaControl, document.Parameters, errors);
        CheckAction(document, lookup, errors);
        CheckConditions(document.Conditions, errors);
        CheckSecrets(document, errors);

        return new ValidationResult(errors);
    }

    /// <summary>Validates a custom sensor document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The errors; empty when the document is valid.</returns>
    public static ValidationResult ValidateSensor(SensorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<ValidationError>();

        CheckHeader(document, errors);
        CheckId(document.Id, "/id", errors);
        CheckName(document.Name, "/name", errors);
        CheckDescriptionAndIcon(document.Description, document.Icon, errors);

        if (!SensorKindSet.Contains(document.Kind ?? string.Empty))
        {
            Add(errors, "/kind", ValidationCodes.Invalid, "kind must be sensor or binary_sensor.");
        }

        bool valueTypeKnown = SensorValueTypeSet.Contains(document.ValueType ?? string.Empty);
        if (!valueTypeKnown)
        {
            Add(errors, "/valueType", ValidationCodes.Invalid, "valueType must be number, string, bool, timestamp or enum.");
        }
        else if (string.Equals(document.Kind, "binary_sensor", StringComparison.Ordinal)
            && !string.Equals(document.ValueType, "bool", StringComparison.Ordinal))
        {
            Add(errors, "/valueType", ValidationCodes.Invalid, "A binary_sensor must have the valueType bool.");
        }

        CheckSensorOptions(document, valueTypeKnown, errors);

        if (document.Precision is < 0)
        {
            Add(errors, "/precision", ValidationCodes.Invalid, "precision must not be negative.");
        }

        CheckNonNegative(document.IntervalSeconds, "/intervalSeconds", errors);
        CheckNonNegative(document.Deadband, "/deadband", errors);
        CheckSensorSource(document, errors);

        return new ValidationResult(errors);
    }

    /// <summary>Validates the metric override document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The errors; empty when the document is valid.</returns>
    public static ValidationResult ValidateMetricOverrides(MetricOverridesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<ValidationError>();

        CheckHeader(document, errors);

        if (document.Overrides is null)
        {
            Add(errors, "/overrides", ValidationCodes.Required, "overrides is required.");
            return new ValidationResult(errors);
        }

        foreach ((string metricId, MetricOverride? metricOverride) in document.Overrides)
        {
            string path = JsonPointer.Member("/overrides", metricId);
            if (!MetricIdPattern.IsMatch(metricId))
            {
                Add(errors, path, ValidationCodes.Invalid, "The key is not a valid metric id.");
            }

            if (metricOverride is null)
            {
                Add(errors, path, ValidationCodes.Required, "The override must be an object.");
                continue;
            }

            CheckNonNegative(metricOverride.IntervalSeconds, JsonPointer.Member(path, "intervalSeconds"), errors);
            CheckNonNegative(metricOverride.Deadband, JsonPointer.Member(path, "deadband"), errors);
        }

        return new ValidationResult(errors);
    }

    /// <summary>Validates the agent settings document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The errors; empty when the document is valid.</returns>
    public static ValidationResult ValidateSettings(AgentSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<ValidationError>();

        CheckHeader(document, errors);
        CheckName(document.Name, "/name", errors);

        CheckPort(document.UiPort, "/uiPort", errors);
        CheckPort(document.LinkPort, "/linkPort", errors);
        if (document.EffectiveUiPort == document.EffectiveLinkPort)
        {
            Add(errors, "/linkPort", ValidationCodes.Conflict, "uiPort and linkPort must differ.");
        }

        if (document.Logging?.Level is { } level && !LogLevelSet.Contains(level))
        {
            Add(errors, "/logging/level", ValidationCodes.Invalid, "level must be trace, debug, information, warning, error or critical.");
        }

        if (document.Retention?.RunHistoryDays is < 1)
        {
            Add(errors, "/retention/runHistoryDays", ValidationCodes.Invalid, "runHistoryDays must be at least 1.");
        }

        if (document.Retention?.AuditLogDays is < 1)
        {
            Add(errors, "/retention/auditLogDays", ValidationCodes.Invalid, "auditLogDays must be at least 1.");
        }

        CheckPrometheus(document.Prometheus, errors);
        CheckReverseLink(document.ReverseLink, errors);
        CheckHotkeys(document.Hotkeys, errors);

        if (document.Updates?.IntervalHours is { } hours
            && (hours < AgentSettingsDefaults.MinUpdateIntervalHours || hours > AgentSettingsDefaults.MaxUpdateIntervalHours))
        {
            Add(
                errors,
                "/updates/intervalHours",
                ValidationCodes.Invalid,
                $"intervalHours must be between {AgentSettingsDefaults.MinUpdateIntervalHours} and {AgentSettingsDefaults.MaxUpdateIntervalHours}.");
        }

        return new ValidationResult(errors);
    }

    /// <summary>
    /// Validates a mode group document (PLAN.md S06, T717): the header, id and name, <c>onBoot</c> and <c>onError</c>, unique mode ids, a
    /// <c>defaultMode</c> that is one of the modes and the steps of every mode. A step is checked like the step of a sequence; in addition
    /// it cannot run the generated command of a mode group (a step cannot switch a mode).
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="lookup">
    /// Optional lookup of the stored commands by id. When it is given, every step must name an existing command (a stored one or one of
    /// the <see cref="SystemCommands"/>), and the group id must not be taken by a stored command <c>mode_&lt;id&gt;</c>, which the generated
    /// command would shadow. Without it only what the document itself shows is checked (the store reads documents without it).
    /// </param>
    /// <returns>The errors; empty when the document is valid.</returns>
    public static ValidationResult ValidateModeGroup(ModeGroupDocument document, Func<string, CommandDocument?>? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<ValidationError>();

        CheckHeader(document, errors);
        CheckId(document.Id, "/id", errors);
        if (document.Id is { Length: > MaxModeGroupIdLength })
        {
            Add(errors, "/id", ValidationCodes.Invalid, "The id must not be longer than 58 characters so that the generated command id mode_<id> is a valid id.");
        }
        else if (!string.IsNullOrEmpty(document.Id) && lookup?.Invoke(ModeNames.CommandId(document.Id)) is not null)
        {
            Add(errors, "/id", ValidationCodes.Conflict, $"A command with the id '{ModeNames.CommandId(document.Id)}' exists; the mode group provides a command with this id.");
        }

        CheckName(document.Name, "/name", errors);
        CheckDescriptionAndIcon(document.Description, document.Icon, errors);

        if (!ModeBootSet.Contains(document.OnBoot ?? string.Empty))
        {
            Add(errors, "/onBoot", ValidationCodes.Invalid, "onBoot must be keep, reset or apply_default.");
        }

        if (!string.Equals(document.OnError, SequenceErrorModes.Stop, StringComparison.Ordinal)
            && !string.Equals(document.OnError, SequenceErrorModes.Continue, StringComparison.Ordinal))
        {
            Add(errors, "/onError", ValidationCodes.Invalid, "onError must be stop or continue.");
        }

        var modeIds = new HashSet<string>(StringComparer.Ordinal);
        if (document.Modes is null || document.Modes.Count == 0)
        {
            Add(errors, "/modes", ValidationCodes.Required, "A mode group needs at least one mode.");
        }
        else
        {
            for (int index = 0; index < document.Modes.Count; index++)
            {
                CheckMode(document, document.Modes[index], JsonPointer.Index("/modes", index), modeIds, lookup, errors);
            }
        }

        if (string.IsNullOrEmpty(document.DefaultMode))
        {
            Add(errors, "/defaultMode", ValidationCodes.Required, "defaultMode is required.");
        }
        else if (modeIds.Count > 0 && !modeIds.Contains(document.DefaultMode))
        {
            Add(errors, "/defaultMode", ValidationCodes.UnknownReference, "defaultMode must be the id of one of the modes.");
        }

        return new ValidationResult(errors);
    }

    private static void CheckMode(
        ModeGroupDocument group,
        ModeDefinition? mode,
        string path,
        HashSet<string> modeIds,
        Func<string, CommandDocument?>? lookup,
        List<ValidationError> errors)
    {
        if (mode is null)
        {
            Add(errors, path, ValidationCodes.Required, "A mode must be an object.");
            return;
        }

        string idPath = JsonPointer.Member(path, "id");
        CheckId(mode.Id, idPath, errors);
        if (!string.IsNullOrEmpty(mode.Id) && !modeIds.Add(mode.Id))
        {
            Add(errors, idPath, ValidationCodes.Duplicate, "Mode ids must be unique in a group.");
        }

        CheckName(mode.Name, JsonPointer.Member(path, "name"), errors);
        CheckModeSteps(group, mode.Enter, JsonPointer.Member(path, "enter"), lookup, errors);
        CheckModeSteps(group, mode.Exit, JsonPointer.Member(path, "exit"), lookup, errors);
    }

    private static void CheckModeSteps(
        ModeGroupDocument group,
        IReadOnlyList<SequenceStep>? steps,
        string path,
        Func<string, CommandDocument?>? lookup,
        List<ValidationError> errors)
    {
        if (steps is null)
        {
            return;
        }

        for (int index = 0; index < steps.Count; index++)
        {
            string stepPath = JsonPointer.Index(path, index);
            SequenceStep step = steps[index];
            CheckStep(step, stepPath, errors);

            if (step?.Command is not { Length: > 0 } command || !IdPattern.IsMatch(command))
            {
                continue;
            }

            string commandPath = JsonPointer.Member(stepPath, "command");
            if (string.Equals(command, ModeNames.CommandId(group.Id ?? string.Empty), StringComparison.Ordinal))
            {
                Add(errors, commandPath, ValidationCodes.Conflict, "A step cannot switch a mode.");
            }
            else if (lookup is not null && SystemCommands.TryGet(command) is null && lookup(command) is null)
            {
                // The generated command of a mode group is not a stored command, so it ends up here as well; say what is wrong.
                if (ModeNames.TryGetGroupId(command, out _))
                {
                    Add(errors, commandPath, ValidationCodes.Conflict, "A step cannot switch a mode, and there is no stored command with this id.");
                }
                else
                {
                    Add(errors, commandPath, ValidationCodes.UnknownReference, "There is no command with this id.");
                }
            }
        }
    }

    private static void CheckReverseLink(ReverseLinkSettings? reverseLink, List<ValidationError> errors)
    {
        if (reverseLink is null)
        {
            return;
        }

        if (reverseLink.Url is not null && OutboundLinkRules.CheckUrl(reverseLink.Url) is { } urlProblem)
        {
            Add(errors, "/reverseLink/url", ValidationCodes.Invalid, urlProblem);
        }

        if (reverseLink.Enabled == true && string.IsNullOrEmpty(reverseLink.Url))
        {
            Add(errors, "/reverseLink/url", ValidationCodes.Required, "url is required when the reverse link is enabled.");
        }

        if (reverseLink.Fingerprint is not null && !OutboundLinkRules.TryNormalizeFingerprint(reverseLink.Fingerprint, out _))
        {
            Add(errors, "/reverseLink/fingerprint", ValidationCodes.Invalid, "fingerprint must be a SHA-256 fingerprint of 64 hexadecimal digits.");
        }
    }

    /// <summary>
    /// The hotkeys (T708): a name that fits the id pattern and is unique, keys that <see cref="HotkeyGesture"/>
    /// understands, and no combination twice among the enabled hotkeys. Whether Windows grants a combination cannot be known here;
    /// the SessionHost reports that as a conflict at registration time.
    /// </summary>
    private static void CheckHotkeys(IReadOnlyList<HotkeySettings>? hotkeys, List<ValidationError> errors)
    {
        if (hotkeys is null)
        {
            return;
        }

        if (hotkeys.Count > MaxHotkeys)
        {
            Add(errors, "/hotkeys", ValidationCodes.Invalid, $"At most {MaxHotkeys} hotkeys can be defined.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var combinations = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < hotkeys.Count; index++)
        {
            string path = JsonPointer.Index("/hotkeys", index);
            HotkeySettings? hotkey = hotkeys[index];
            if (hotkey is null)
            {
                Add(errors, path, ValidationCodes.Invalid, "A hotkey is expected.");
                continue;
            }

            if (string.IsNullOrEmpty(hotkey.Name) || !IdPattern.IsMatch(hotkey.Name))
            {
                Add(errors, path + "/name", ValidationCodes.Invalid, "name must start with a lower-case letter and have at most 63 lower-case letters, digits or underscores.");
            }
            else if (!names.Add(hotkey.Name))
            {
                Add(errors, path + "/name", ValidationCodes.Duplicate, "Each hotkey name can be used once.");
            }

            if (!HotkeyGesture.TryParse(hotkey.Keys, out HotkeyGesture? gesture, out string? problem))
            {
                Add(errors, path + "/keys", ValidationCodes.Invalid, problem ?? "The keys are not valid.");
            }
            else if (hotkey.EffectiveEnabled && !combinations.Add(gesture!.Canonical))
            {
                Add(errors, path + "/keys", ValidationCodes.Conflict, "Another enabled hotkey already uses " + gesture.Canonical + ".");
            }
        }
    }

    private static void CheckPrometheus(PrometheusSettings? prometheus, List<ValidationError> errors)
    {
        if (prometheus?.Metrics is not { } metrics)
        {
            return;
        }

        if (metrics.Count > MaxPrometheusMetrics)
        {
            Add(errors, "/prometheus/metrics", ValidationCodes.Invalid, $"At most {MaxPrometheusMetrics} metrics can be exposed.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < metrics.Count; index++)
        {
            string path = JsonPointer.Index("/prometheus/metrics", index);
            string? id = metrics[index];
            if (string.IsNullOrEmpty(id) || !MetricIdPattern.IsMatch(id))
            {
                Add(errors, path, ValidationCodes.Invalid, "A metric id such as cpu.usage or custom.my_sensor is expected.");
            }
            else if (!seen.Add(id))
            {
                Add(errors, path, ValidationCodes.Duplicate, "Each metric can be listed once.");
            }
        }
    }

    // ------------------------------------------------------------------
    // Common checks
    // ------------------------------------------------------------------

    private static void Add(List<ValidationError> errors, string path, string code, string message)
    {
        errors.Add(new ValidationError(path, code, message));
    }

    private static void CheckHeader(IConfigDocument document, List<ValidationError> errors, int maxVersion = DocumentSchema.CurrentVersion)
    {
        if (document.SchemaVersion < 1)
        {
            Add(errors, "/schemaVersion", ValidationCodes.Invalid, "schemaVersion must be at least 1.");
        }
        else if (document.SchemaVersion > maxVersion)
        {
            Add(errors, "/schemaVersion", ValidationCodes.Unsupported, "The schemaVersion is newer than this agent understands.");
        }

        if (document.Revision is < 0)
        {
            Add(errors, "/revision", ValidationCodes.Invalid, "revision must not be negative.");
        }

        if (document.Origin is not null && !OriginSet.Contains(document.Origin))
        {
            Add(errors, "/origin", ValidationCodes.Invalid, "origin must be local or hub.");
        }
    }

    private static void CheckId(string? id, string path, List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(id))
        {
            Add(errors, path, ValidationCodes.Required, "An id is required.");
        }
        else if (!IdPattern.IsMatch(id))
        {
            Add(errors, path, ValidationCodes.Invalid, "The id must match ^[a-z][a-z0-9_]{0,62}$.");
        }
    }

    private static void CheckName(string? name, string path, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Add(errors, path, ValidationCodes.Required, "A name is required.");
        }
        else if (name.Length > MaxNameLength)
        {
            Add(errors, path, ValidationCodes.Invalid, "The name must not be longer than 100 characters.");
        }
        else if (name.Any(char.IsControl))
        {
            Add(errors, path, ValidationCodes.Invalid, "The name must not contain control characters.");
        }
    }

    private static void CheckDescriptionAndIcon(string? description, string? icon, List<ValidationError> errors)
    {
        if (description is not null && description.Length > MaxDescriptionLength)
        {
            Add(errors, "/description", ValidationCodes.Invalid, "The description must not be longer than 2000 characters.");
        }

        if (icon is not null && !IconPattern.IsMatch(icon))
        {
            Add(errors, "/icon", ValidationCodes.Invalid, "The icon must look like mdi:monitor-multiple.");
        }
    }

    private static void CheckNonNegative(double? value, string path, List<ValidationError> errors)
    {
        if (value is { } number && (!double.IsFinite(number) || number < 0))
        {
            Add(errors, path, ValidationCodes.Invalid, "The value must be a number that is not negative.");
        }
    }

    private static void CheckPort(int? port, string path, List<ValidationError> errors)
    {
        if (port is < 1 or > 65535)
        {
            Add(errors, path, ValidationCodes.Invalid, "A port must be between 1 and 65535.");
        }
    }

    // ------------------------------------------------------------------
    // Parameters (PLAN.md S06, table "Parameters")
    // ------------------------------------------------------------------

    private static void CheckParameters(IReadOnlyList<ParameterDefinition>? parameters, List<ValidationError> errors)
    {
        if (parameters is null)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < parameters.Count; index++)
        {
            string path = JsonPointer.Index("/parameters", index);
            ParameterDefinition parameter = parameters[index];
            if (parameter is null)
            {
                Add(errors, path, ValidationCodes.Required, "A parameter must be an object.");
                continue;
            }

            if (string.IsNullOrEmpty(parameter.Name))
            {
                Add(errors, JsonPointer.Member(path, "name"), ValidationCodes.Required, "A parameter name is required.");
            }
            else if (!ParameterNamePattern.IsMatch(parameter.Name))
            {
                Add(errors, JsonPointer.Member(path, "name"), ValidationCodes.Invalid, "The name must match ^[A-Za-z][A-Za-z0-9_]{0,31}$.");
            }
            else if (!names.Add(parameter.Name))
            {
                Add(errors, JsonPointer.Member(path, "name"), ValidationCodes.Duplicate, "Parameter names must be unique, ignoring case.");
            }

            CheckParameter(parameter, path, errors);
        }
    }

    private static void CheckParameter(ParameterDefinition parameter, string path, List<ValidationError> errors)
    {
        if (!ParameterTypeSet.Contains(parameter.Type ?? string.Empty))
        {
            Add(errors, JsonPointer.Member(path, "type"), ValidationCodes.Invalid, "type must be string, int, float, bool or enum.");
            return;
        }

        bool isNumber = parameter.Type is ParameterTypes.WholeNumber or ParameterTypes.Real;
        bool isText = parameter.Type == ParameterTypes.Text;
        bool isChoice = parameter.Type == ParameterTypes.Choice;

        CheckApplicable(parameter.Min is not null, isNumber, "min", path, "int and float", errors);
        CheckApplicable(parameter.Max is not null, isNumber, "max", path, "int and float", errors);
        CheckApplicable(parameter.Step is not null, isNumber, "step", path, "int and float", errors);
        CheckApplicable(parameter.Pattern is not null, isText, "pattern", path, "string", errors);
        CheckApplicable(parameter.MaxLength is not null, isText, "maxLength", path, "string", errors);
        CheckApplicable(parameter.Options is not null, isChoice, "options", path, "enum", errors);
        CheckApplicable(parameter.OptionsFrom is not null, isChoice, "optionsFrom", path, "enum", errors);

        if (isNumber)
        {
            CheckNumberBounds(parameter, path, errors);
        }

        if (isText)
        {
            CheckTextRules(parameter, path, errors);
        }

        if (isChoice)
        {
            CheckChoiceRules(parameter, path, errors);
        }

        if (parameter.Default is { } defaultValue && defaultValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            ParameterValues.Convert(parameter, defaultValue, JsonPointer.Member(path, "default"), coerce: false, errors);
        }
    }

    private static void CheckApplicable(bool present, bool applicable, string member, string path, string appliesTo, List<ValidationError> errors)
    {
        if (present && !applicable)
        {
            Add(errors, JsonPointer.Member(path, member), ValidationCodes.NotApplicable, $"{member} applies only to {appliesTo} parameters.");
        }
    }

    private static void CheckNumberBounds(ParameterDefinition parameter, string path, List<ValidationError> errors)
    {
        bool integral = parameter.Type == ParameterTypes.WholeNumber;

        CheckBound(parameter.Min, "min", integral, path, errors);
        CheckBound(parameter.Max, "max", integral, path, errors);

        if (parameter.Min is { } min && parameter.Max is { } max && min > max)
        {
            Add(errors, JsonPointer.Member(path, "min"), ValidationCodes.Invalid, "min must not be greater than max.");
        }

        if (parameter.Step is { } step)
        {
            if (!double.IsFinite(step) || step <= 0)
            {
                Add(errors, JsonPointer.Member(path, "step"), ValidationCodes.Invalid, "step must be greater than 0.");
            }
            else if (integral && step != Math.Floor(step))
            {
                Add(errors, JsonPointer.Member(path, "step"), ValidationCodes.Invalid, "step of an int parameter must be a whole number.");
            }
        }
    }

    private static void CheckBound(double? bound, string member, bool integral, string path, List<ValidationError> errors)
    {
        if (bound is not { } value)
        {
            return;
        }

        if (!double.IsFinite(value))
        {
            Add(errors, JsonPointer.Member(path, member), ValidationCodes.Invalid, $"{member} must be a finite number.");
        }
        else if (integral && value != Math.Floor(value))
        {
            Add(errors, JsonPointer.Member(path, member), ValidationCodes.Invalid, $"{member} of an int parameter must be a whole number.");
        }
    }

    private static void CheckTextRules(ParameterDefinition parameter, string path, List<ValidationError> errors)
    {
        if (parameter.MaxLength is < 1)
        {
            Add(errors, JsonPointer.Member(path, "maxLength"), ValidationCodes.Invalid, "maxLength must be at least 1.");
        }

        if (parameter.Pattern is not null)
        {
            try
            {
                _ = new Regex(parameter.Pattern, RegexOptions.CultureInvariant, ParameterValues.PatternTimeout);
            }
            catch (ArgumentException)
            {
                Add(errors, JsonPointer.Member(path, "pattern"), ValidationCodes.InvalidPattern, "The pattern is not a valid .NET regular expression.");
            }
        }
    }

    private static void CheckChoiceRules(ParameterDefinition parameter, string path, List<ValidationError> errors)
    {
        bool hasOptions = parameter.Options is not null;
        bool hasOptionsFrom = !string.IsNullOrEmpty(parameter.OptionsFrom);

        if (hasOptions && hasOptionsFrom)
        {
            Add(errors, JsonPointer.Member(path, "optionsFrom"), ValidationCodes.Conflict, "optionsFrom replaces options; set only one of them.");
        }
        else if (!hasOptions && !hasOptionsFrom)
        {
            Add(errors, JsonPointer.Member(path, "options"), ValidationCodes.Required, "An enum parameter needs a non-empty options list or optionsFrom.");
        }

        if (parameter.Options is { } options)
        {
            if (options.Count == 0)
            {
                Add(errors, JsonPointer.Member(path, "options"), ValidationCodes.Required, "options must not be empty.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < options.Count; index++)
            {
                string optionPath = JsonPointer.Index(JsonPointer.Member(path, "options"), index);
                if (string.IsNullOrEmpty(options[index]))
                {
                    Add(errors, optionPath, ValidationCodes.Invalid, "An option must be a non-empty string.");
                }
                else if (!seen.Add(options[index]))
                {
                    Add(errors, optionPath, ValidationCodes.Duplicate, "Options must be unique.");
                }
            }
        }

        if (parameter.OptionsFrom is not null && !MetricIdPattern.IsMatch(parameter.OptionsFrom))
        {
            Add(errors, JsonPointer.Member(path, "optionsFrom"), ValidationCodes.Invalid, "optionsFrom must be a metric id.");
        }
    }

    // ------------------------------------------------------------------
    // haControl (PLAN.md S06, table "haControl")
    // ------------------------------------------------------------------

    private static void CheckHaControl(HaControl? control, IReadOnlyList<ParameterDefinition>? parameters, List<ValidationError> errors)
    {
        if (control is null)
        {
            return;
        }

        if (!HaControlSet.Contains(control.Type ?? string.Empty))
        {
            Add(errors, "/haControl/type", ValidationCodes.Invalid, "haControl.type must be none, button, switch, select, number or text.");
            return;
        }

        if (control.StateMetric is not null && !MetricIdPattern.IsMatch(control.StateMetric))
        {
            Add(errors, "/haControl/stateMetric", ValidationCodes.Invalid, "stateMetric must be a metric id.");
        }

        switch (control.Type)
        {
            case HaControlTypes.None:
                CheckNoParameter(control, "none", errors);
                break;
            case HaControlTypes.Button:
                CheckNoParameter(control, "button", errors);
                CheckButtonParameters(parameters, errors);
                break;
            case HaControlTypes.Switch:
                CheckControlParameter(control, parameters, SwitchParameterTypes, "bool", errors);
                break;
            case HaControlTypes.Select:
                CheckControlParameter(control, parameters, SelectParameterTypes, "enum", errors);
                break;
            case HaControlTypes.Number:
                CheckControlParameter(control, parameters, NumberParameterTypes, "int or float", errors);
                break;
            case HaControlTypes.Text:
                CheckControlParameter(control, parameters, TextParameterTypes, "string", errors);
                break;
        }
    }

    private static void CheckNoParameter(HaControl control, string type, List<ValidationError> errors)
    {
        if (control.Parameter is not null)
        {
            Add(errors, "/haControl/parameter", ValidationCodes.NotApplicable, $"A {type} control does not use a parameter.");
        }
    }

    private static void CheckButtonParameters(IReadOnlyList<ParameterDefinition>? parameters, List<ValidationError> errors)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (ParameterDefinition parameter in parameters)
        {
            bool hasDefault = parameter?.Default is { } value
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            if (parameter is { Required: true } && !hasDefault)
            {
                Add(errors, "/haControl/type", ValidationCodes.Invalid, $"A button runs the command with defaults, but parameter '{parameter.Name}' is required and has no default.");
            }
        }
    }

    private static void CheckControlParameter(
        HaControl control,
        IReadOnlyList<ParameterDefinition>? parameters,
        string[] allowedTypes,
        string allowedDescription,
        List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(control.Parameter))
        {
            Add(errors, "/haControl/parameter", ValidationCodes.Required, $"A {control.Type} control needs a parameter of type {allowedDescription}.");
            return;
        }

        ParameterDefinition? target = parameters?.FirstOrDefault(p => p is not null && string.Equals(p.Name, control.Parameter, StringComparison.Ordinal));
        if (target is null)
        {
            Add(errors, "/haControl/parameter", ValidationCodes.UnknownReference, "The command declares no parameter with this name.");
        }
        else if (!allowedTypes.Contains(target.Type, StringComparer.Ordinal))
        {
            Add(errors, "/haControl/parameter", ValidationCodes.TypeMismatch, $"A {control.Type} control needs a parameter of type {allowedDescription}.");
        }
    }

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Conditions and secrets (schema version 2, T705, T706)
    // ------------------------------------------------------------------

    private static void CheckConditions(CommandConditions? conditions, List<ValidationError> errors)
    {
        if (conditions is null)
        {
            return;
        }

        if (conditions.Session is not null && !SessionRequirementSet.Contains(conditions.Session))
        {
            Add(errors, "/conditions/session", ValidationCodes.Invalid, "session must be any, logged_in, unlocked or locked.");
        }

        if (conditions.TimeWindow is not { } window)
        {
            return;
        }

        const string windowPath = "/conditions/timeWindow";
        CheckWindowTime(window.From, JsonPointer.Member(windowPath, "from"), errors);
        CheckWindowTime(window.To, JsonPointer.Member(windowPath, "to"), errors);

        if (window.Days is null)
        {
            return;
        }

        var days = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < window.Days.Count; index++)
        {
            string path = JsonPointer.Index(JsonPointer.Member(windowPath, "days"), index);
            string? day = window.Days[index];
            if (day is null || !WeekdayNames.All.Contains(day, StringComparer.Ordinal))
            {
                Add(errors, path, ValidationCodes.Invalid, "A day must be mon, tue, wed, thu, fri, sat or sun.");
            }
            else if (!days.Add(day))
            {
                Add(errors, path, ValidationCodes.Duplicate, "Each day may appear once.");
            }
        }
    }

    private static void CheckWindowTime(string? time, string path, List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(time))
        {
            Add(errors, path, ValidationCodes.Required, "A time of day such as 22:30 is required.");
        }
        else if (!CommandConditionEvaluator.TryParseTime(time, out _))
        {
            Add(errors, path, ValidationCodes.Invalid, "The time must look like 22:30 (HH:mm, 24-hour clock).");
        }
    }

    private static void CheckSecrets(CommandDocument document, List<ValidationError> errors)
    {
        if (document.Secrets is not { Count: > 0 } secrets)
        {
            return;
        }

        if (document.Action is not (PowerShellAction or BashAction or ProcessAction))
        {
            Add(errors, "/secrets", ValidationCodes.NotApplicable, "Secrets can only be used by powershell, bash and process actions.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < secrets.Count; index++)
        {
            string path = JsonPointer.Index("/secrets", index);
            string? name = secrets[index];
            if (string.IsNullOrEmpty(name))
            {
                Add(errors, path, ValidationCodes.Required, "A secret name is required.");
            }
            else if (!NamedSecretNames.IsValid(name))
            {
                Add(errors, path, ValidationCodes.Invalid, "The name must match ^[a-z][a-z0-9_]{0,62}$.");
            }
            else if (!seen.Add(name))
            {
                Add(errors, path, ValidationCodes.Duplicate, "Each secret may be listed once.");
            }
        }
    }

    private static void CheckAction(CommandDocument document, Func<string, CommandDocument?>? lookup, List<ValidationError> errors)
    {
        switch (document.Action)
        {
            case null:
                Add(errors, "/action", ValidationCodes.Required, "An action is required.");
                break;
            case PowerShellAction powerShell:
                CheckPowerShell(powerShell.Source, powerShell.Code, powerShell.Script, powerShell.Engine, "/action", errors);
                break;
            case BashAction bash:
                CheckBash(bash, "/action", errors);
                break;
            case ProcessAction process:
                CheckProcess(process, document.Parameters, errors);
                break;
            case BuiltinAction builtin:
                if (string.IsNullOrEmpty(builtin.Name))
                {
                    Add(errors, "/action/name", ValidationCodes.Required, "A builtin action needs a name.");
                }
                else if (!BuiltinNamePattern.IsMatch(builtin.Name))
                {
                    Add(errors, "/action/name", ValidationCodes.Invalid, "The name must look like power.shutdown.");
                }

                break;
            case SequenceAction sequence:
                CheckSequence(document, sequence, lookup, errors);
                break;
            default:
                Add(errors, "/action/type", ValidationCodes.Unsupported, "The action type must be powershell, bash, process, builtin or sequence.");
                break;
        }
    }

    private static void CheckBash(BashAction bash, string path, List<ValidationError> errors)
    {
        if (string.Equals(bash.Source, BashSources.Inline, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(bash.Code))
            {
                Add(errors, JsonPointer.Member(path, "code"), ValidationCodes.Required, "Inline bash needs a script.");
            }
        }
        else if (string.Equals(bash.Source, BashSources.File, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(bash.Script))
            {
                Add(errors, JsonPointer.Member(path, "script"), ValidationCodes.Required, "A bash file source needs a script name.");
            }
            else if (!ScriptNamePattern.IsMatch(bash.Script) || bash.Script.Contains("..", StringComparison.Ordinal))
            {
                Add(errors, JsonPointer.Member(path, "script"), ValidationCodes.Invalid, "The script must be a plain file name in the scripts directory.");
            }
        }
        else if (string.IsNullOrEmpty(bash.Source))
        {
            Add(errors, JsonPointer.Member(path, "source"), ValidationCodes.Required, "source is required.");
        }
        else
        {
            Add(errors, JsonPointer.Member(path, "source"), ValidationCodes.Invalid, "source must be inline or file.");
        }
    }

    private static void CheckPowerShell(string? source, string? code, string? script, string? engine, string path, List<ValidationError> errors)
    {
        if (string.Equals(source, PowerShellSources.Inline, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                Add(errors, JsonPointer.Member(path, "code"), ValidationCodes.Required, "Inline PowerShell needs code.");
            }
        }
        else if (string.Equals(source, PowerShellSources.File, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                Add(errors, JsonPointer.Member(path, "script"), ValidationCodes.Required, "A PowerShell file source needs a script name.");
            }
            else if (!ScriptNamePattern.IsMatch(script) || script.Contains("..", StringComparison.Ordinal))
            {
                Add(errors, JsonPointer.Member(path, "script"), ValidationCodes.Invalid, "The script must be a plain file name in the scripts directory.");
            }
        }
        else if (string.IsNullOrEmpty(source))
        {
            Add(errors, JsonPointer.Member(path, "source"), ValidationCodes.Required, "source is required.");
        }
        else
        {
            Add(errors, JsonPointer.Member(path, "source"), ValidationCodes.Invalid, "source must be inline or file.");
        }

        if (engine is null || !EngineSet.Contains(engine))
        {
            Add(errors, JsonPointer.Member(path, "engine"), ValidationCodes.Invalid, "engine must be auto, pwsh or powershell.");
        }
    }

    private static void CheckProcess(ProcessAction process, IReadOnlyList<ParameterDefinition>? parameters, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(process.File))
        {
            Add(errors, "/action/file", ValidationCodes.Required, "A process action needs a file.");
        }

        if (process.Args is null)
        {
            return;
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (ParameterDefinition parameter in parameters)
            {
                if (parameter is not null && !string.IsNullOrEmpty(parameter.Name))
                {
                    declared.Add(parameter.Name);
                }
            }
        }

        for (int index = 0; index < process.Args.Count; index++)
        {
            string path = JsonPointer.Index("/action/args", index);
            string? argument = process.Args[index];
            if (argument is null)
            {
                Add(errors, path, ValidationCodes.Invalid, "An argument must be a string.");
                continue;
            }

            foreach (Match match in PlaceholderPattern.Matches(argument))
            {
                string name = match.Groups["name"].Value;
                if (ContextVariableSet.Contains(name))
                {
                    continue;
                }

                if (!PlaceholderNamePattern.IsMatch(name))
                {
                    Add(errors, path, ValidationCodes.Invalid, "A {{placeholder}} must name a variable or one of qa.run_id, qa.source, qa.command_id, qa.agent_name.");
                }
                else if (!declared.Contains(name))
                {
                    Add(errors, path, ValidationCodes.UnknownVariable, $"The placeholder {{{{{name}}}}} names a variable that the command does not declare.");
                }
            }
        }
    }

    private static void CheckSequence(CommandDocument document, SequenceAction sequence, Func<string, CommandDocument?>? lookup, List<ValidationError> errors)
    {
        if (sequence.Steps is null || sequence.Steps.Count == 0)
        {
            Add(errors, "/action/steps", ValidationCodes.Required, "A sequence needs at least one step.");
        }
        else
        {
            for (int index = 0; index < sequence.Steps.Count; index++)
            {
                CheckStep(sequence.Steps[index], JsonPointer.Index("/action/steps", index), errors);
            }

            DetectCycles(document, sequence, lookup, errors);
        }

        if (!string.Equals(sequence.OnError, SequenceErrorModes.Stop, StringComparison.Ordinal)
            && !string.Equals(sequence.OnError, SequenceErrorModes.Continue, StringComparison.Ordinal))
        {
            Add(errors, "/action/onError", ValidationCodes.Invalid, "onError must be stop or continue.");
        }
    }

    private static void CheckStep(SequenceStep step, string path, List<ValidationError> errors)
    {
        if (step is null)
        {
            Add(errors, path, ValidationCodes.Required, "A step must be an object.");
            return;
        }

        bool hasCommand = step.Command is not null;
        bool hasDelay = step.DelaySeconds is not null;

        if (hasCommand && hasDelay)
        {
            Add(errors, path, ValidationCodes.Conflict, "A step is either a command or a delay, not both.");
        }
        else if (!hasCommand && !hasDelay)
        {
            Add(errors, path, ValidationCodes.Required, "A step needs a command or delaySeconds.");
        }

        if (step.Command is not null && !IdPattern.IsMatch(step.Command))
        {
            Add(errors, JsonPointer.Member(path, "command"), ValidationCodes.Invalid, "The command must be a command id.");
        }

        if (step.DelaySeconds is { } delay && (!double.IsFinite(delay) || delay < 0))
        {
            Add(errors, JsonPointer.Member(path, "delaySeconds"), ValidationCodes.Invalid, "delaySeconds must be a number that is not negative.");
        }

        if (step.Vars is null)
        {
            return;
        }

        if (!hasCommand)
        {
            Add(errors, JsonPointer.Member(path, "vars"), ValidationCodes.NotApplicable, "vars apply only to command steps.");
        }

        foreach ((string name, JsonElement value) in step.Vars)
        {
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            {
                Add(errors, JsonPointer.Member(JsonPointer.Member(path, "vars"), name), ValidationCodes.TypeMismatch, "A variable value must be a string, number or boolean.");
            }
        }
    }

    // ------------------------------------------------------------------
    // Sequence cycle detection (the hook of T103)
    // ------------------------------------------------------------------

    private static void DetectCycles(CommandDocument document, SequenceAction sequence, Func<string, CommandDocument?>? lookup, List<ValidationError> errors)
    {
        if (string.IsNullOrEmpty(document.Id))
        {
            return;
        }

        for (int index = 0; index < sequence.Steps.Count; index++)
        {
            string? target = sequence.Steps[index]?.Command;
            if (string.IsNullOrEmpty(target))
            {
                continue;
            }

            var trail = new List<string> { document.Id };
            var visited = new HashSet<string>(StringComparer.Ordinal);
            if (Reaches(target, document.Id, lookup, visited, trail))
            {
                Add(errors, JsonPointer.Member(JsonPointer.Index("/action/steps", index), "command"), ValidationCodes.Cycle, "Sequences must not call each other in a cycle: " + string.Join(" -> ", trail));
            }
        }
    }

    private static bool Reaches(string current, string root, Func<string, CommandDocument?>? lookup, HashSet<string> visited, List<string> trail)
    {
        if (string.Equals(current, root, StringComparison.Ordinal))
        {
            trail.Add(current);
            return true;
        }

        if (!visited.Add(current))
        {
            return false;
        }

        if (lookup?.Invoke(current)?.Action is not SequenceAction { Steps: { } steps })
        {
            return false;
        }

        trail.Add(current);
        foreach (SequenceStep? step in steps)
        {
            if (step?.Command is { Length: > 0 } next && Reaches(next, root, lookup, visited, trail))
            {
                return true;
            }
        }

        trail.RemoveAt(trail.Count - 1);
        return false;
    }

    // ------------------------------------------------------------------
    // Sensors
    // ------------------------------------------------------------------

    private static void CheckSensorOptions(SensorDocument document, bool valueTypeKnown, List<ValidationError> errors)
    {
        bool isEnum = string.Equals(document.ValueType, "enum", StringComparison.Ordinal);

        if (document.Options is null)
        {
            if (valueTypeKnown && isEnum)
            {
                Add(errors, "/options", ValidationCodes.Required, "An enum sensor needs a non-empty options list.");
            }

            return;
        }

        if (valueTypeKnown && !isEnum)
        {
            Add(errors, "/options", ValidationCodes.NotApplicable, "options apply only to enum sensors.");
            return;
        }

        if (document.Options.Count == 0)
        {
            Add(errors, "/options", ValidationCodes.Required, "options must not be empty.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < document.Options.Count; index++)
        {
            string path = JsonPointer.Index("/options", index);
            if (string.IsNullOrEmpty(document.Options[index]))
            {
                Add(errors, path, ValidationCodes.Invalid, "An option must be a non-empty string.");
            }
            else if (!seen.Add(document.Options[index]))
            {
                Add(errors, path, ValidationCodes.Duplicate, "Options must be unique.");
            }
        }
    }

    private static void CheckSensorSource(SensorDocument document, List<ValidationError> errors)
    {
        switch (document.Source)
        {
            case null:
                Add(errors, "/source", ValidationCodes.Required, "A source is required.");
                break;
            case PowerShellSensorSource powerShell:
                CheckPowerShell(powerShell.Source, powerShell.Code, powerShell.Script, powerShell.Engine, "/source", errors);
                break;
            case ProcessRunningSensorSource process:
                CheckRequiredText(process.Process, "/source/process", errors);
                break;
            case PerfCounterSensorSource counter:
                CheckRequiredText(counter.Category, "/source/category", errors);
                CheckRequiredText(counter.Counter, "/source/counter", errors);
                break;
            case CimQuerySensorSource cim:
                CheckRequiredText(cim.Namespace, "/source/namespace", errors);
                CheckRequiredText(cim.ClassName, "/source/class", errors);
                CheckRequiredText(cim.Property, "/source/property", errors);
                break;
            case ServiceStateSensorSource service:
                CheckRequiredText(service.Service, "/source/service", errors);
                break;
            case StateSensorSource state:
                CheckStateSource(document, state, errors);
                break;
            default:
                Add(errors, "/source/type", ValidationCodes.Unsupported, "The source type must be powershell, process_running, perf_counter, cim_query, service_state or state.");
                break;
        }
    }

    private static void CheckRequiredText(string? value, string path, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, ValidationCodes.Required, "A value is required.");
        }
    }

    private static void CheckStateSource(SensorDocument document, StateSensorSource state, List<ValidationError> errors)
    {
        if (state.InitialValue is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        bool fits = document.ValueType switch
        {
            "number" => value.ValueKind == JsonValueKind.Number,
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "string" or "timestamp" => value.ValueKind == JsonValueKind.String,
            "enum" => value.ValueKind == JsonValueKind.String
                && (document.Options is null || document.Options.Contains(value.GetString()!, StringComparer.Ordinal)),
            _ => true,
        };

        if (!fits)
        {
            Add(errors, "/source/initialValue", ValidationCodes.TypeMismatch, "The initial value does not fit the valueType of the sensor.");
        }
    }
}
