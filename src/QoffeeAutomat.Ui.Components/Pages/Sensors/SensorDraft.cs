using System.Globalization;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Components.Pages.Sensors;

/// <summary>The result of turning a draft into a document.</summary>
/// <param name="Document">The document; meaningful only when <paramref name="Problems"/> is empty.</param>
/// <param name="Problems">What is not a value, as JSON pointers into the document; the agent reports the other problems.</param>
public sealed record SensorBuild(SensorDocument Document, IReadOnlyList<ApiValidationError> Problems);

/// <summary>
/// The sensor that is being edited, as the form holds it: every field is text or a flag, the way the controls give it. Building
/// the document drops what does not apply to the value type and the source type, so that switching the type does not leave stale
/// members behind, and keeps what the form does not show (the origin, a source that this editor cannot author).
/// </summary>
public sealed class SensorDraft
{
    private SensorDocument? _original;

    /// <summary>True until the sensor was saved once.</summary>
    public bool IsNew => _original is null;

    /// <summary>The id; fixed once the sensor exists.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The icon; empty for none.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>Whether the sensor is sampled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether Home Assistant sees the sensor.</summary>
    public bool ExposeToHa { get; set; } = true;

    /// <summary>The value type, one of <see cref="SensorAttributeRules.ValueTypes"/>.</summary>
    public string ValueType { get; set; } = "number";

    /// <summary>The unit; empty for none.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>The device class; empty for none.</summary>
    public string DeviceClass { get; set; } = string.Empty;

    /// <summary>The state class; empty for none.</summary>
    public string StateClass { get; set; } = string.Empty;

    /// <summary>The entity category; empty for none.</summary>
    public string EntityCategory { get; set; } = string.Empty;

    /// <summary>The number of decimals, as typed.</summary>
    public string PrecisionText { get; set; } = string.Empty;

    /// <summary>The options of an enum sensor, one per line.</summary>
    public string OptionsText { get; set; } = string.Empty;

    /// <summary>The sampling interval in seconds, as typed; empty for the default.</summary>
    public string IntervalText { get; set; } = string.Empty;

    /// <summary>The deadband, as typed; empty for none.</summary>
    public string DeadbandText { get; set; } = string.Empty;

    /// <summary>The <c>source.type</c>.</summary>
    public string SourceType { get; set; } = SensorSourceTypes.PowerShell;

    /// <summary>Whether the PowerShell code is inline or a file, see <see cref="PowerShellSources"/>.</summary>
    public string ScriptSource { get; set; } = PowerShellSources.Inline;

    /// <summary>The inline PowerShell code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The name of the script file.</summary>
    public string Script { get; set; } = string.Empty;

    /// <summary>The PowerShell engine, see <see cref="PowerShellEngines"/>.</summary>
    public string Engine { get; set; } = PowerShellEngines.Auto;

    /// <summary>The process name of a <c>process_running</c> sensor.</summary>
    public string Process { get; set; } = string.Empty;

    /// <summary>The performance counter category.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The performance counter.</summary>
    public string Counter { get; set; } = string.Empty;

    /// <summary>The performance counter instance.</summary>
    public string Instance { get; set; } = string.Empty;

    /// <summary>The CIM namespace.</summary>
    public string CimNamespace { get; set; } = @"root\cimv2";

    /// <summary>The CIM class.</summary>
    public string CimClass { get; set; } = string.Empty;

    /// <summary>The CIM property.</summary>
    public string CimProperty { get; set; } = string.Empty;

    /// <summary>The CIM filter.</summary>
    public string CimFilter { get; set; } = string.Empty;

    /// <summary>The service name.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Starts a draft from a stored sensor.</summary>
    /// <param name="sensor">The sensor.</param>
    /// <returns>The draft; it remembers the revision to save against.</returns>
    public static SensorDraft From(SensorDocument sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        var draft = new SensorDraft();
        draft.Adopt(sensor);
        return draft;
    }

    /// <summary>Takes over a sensor as stored (after a save): the draft is no longer new and carries the new revision.</summary>
    /// <param name="sensor">The sensor.</param>
    public void Adopt(SensorDocument sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        _original = sensor;
        Id = sensor.Id;
        Name = sensor.Name;
        Description = sensor.Description ?? string.Empty;
        Icon = sensor.Icon ?? string.Empty;
        Enabled = sensor.Enabled;
        ExposeToHa = sensor.ExposeToHa;
        ValueType = sensor.ValueType;
        Unit = sensor.Unit ?? string.Empty;
        DeviceClass = sensor.DeviceClass ?? string.Empty;
        StateClass = sensor.StateClass ?? string.Empty;
        EntityCategory = sensor.EntityCategory ?? string.Empty;
        PrecisionText = sensor.Precision is { } precision ? precision.ToString(CultureInfo.InvariantCulture) : string.Empty;
        OptionsText = sensor.Options is { } options ? string.Join('\n', options) : string.Empty;
        IntervalText = MetricNumber(sensor.IntervalSeconds);
        DeadbandText = MetricNumber(sensor.Deadband);
        SourceType = sensor.Source.Type;

        switch (sensor.Source)
        {
            case PowerShellSensorSource powerShell:
                ScriptSource = powerShell.Source;
                Code = powerShell.Code ?? string.Empty;
                Script = powerShell.Script ?? string.Empty;
                Engine = powerShell.Engine;
                break;
            case ProcessRunningSensorSource process:
                Process = process.Process;
                break;
            case PerfCounterSensorSource counter:
                Category = counter.Category;
                Counter = counter.Counter;
                Instance = counter.Instance ?? string.Empty;
                break;
            case CimQuerySensorSource cim:
                CimNamespace = cim.Namespace;
                CimClass = cim.ClassName;
                CimProperty = cim.Property;
                CimFilter = cim.Filter ?? string.Empty;
                break;
            case ServiceStateSensorSource service:
                Service = service.Service;
                break;
        }
    }

    /// <summary>Turns the draft into a sensor document.</summary>
    /// <returns>The document, and the problems of the fields that are no values.</returns>
    public SensorBuild Build()
    {
        var problems = new List<ApiValidationError>();
        int? precision = null;
        if (SensorAttributeRules.AllowsPrecision(ValueType) && !string.IsNullOrWhiteSpace(PrecisionText))
        {
            if (int.TryParse(PrecisionText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                precision = parsed;
            }
            else
            {
                problems.Add(new ApiValidationError("/precision", "not_a_number", "This is not a whole number."));
            }
        }

        double? interval = ParseOptional(IntervalText, "/intervalSeconds", problems);
        double? deadband = SensorAttributeRules.AllowsPrecision(ValueType) ? ParseOptional(DeadbandText, "/deadband", problems) : null;

        IReadOnlyList<string>? options = SensorAttributeRules.UsesOptions(ValueType)
            ? [.. OptionsText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]
            : null;

        string valueType = ValueType;
        var document = new SensorDocument(
            _original?.SchemaVersion ?? DocumentSchema.CurrentVersion,
            Id.Trim(),
            Name.Trim(),
            Enabled,
            ExposeToHa,
            SensorAttributeRules.KindFor(valueType),
            valueType,
            BuildSource(),
            Description: NullIfEmpty(Description),
            Icon: NullIfEmpty(Icon),
            EnabledDefault: _original?.EnabledDefault,
            Unit: SensorAttributeRules.AllowsUnit(valueType) ? NullIfEmpty(Unit) : null,
            DeviceClass: SensorAttributeRules.DeviceClassesFor(valueType).Count > 0 ? NullIfEmpty(DeviceClass) : null,
            StateClass: SensorAttributeRules.AllowsStateClass(valueType) ? NullIfEmpty(StateClass) : null,
            EntityCategory: NullIfEmpty(EntityCategory),
            Precision: precision,
            Options: options,
            IntervalSeconds: interval,
            Deadband: deadband,
            Revision: _original?.Revision,
            UpdatedAt: _original?.UpdatedAt,
            Origin: _original?.Origin);
        return new SensorBuild(document, problems);
    }

    private static string? NullIfEmpty(string text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string MetricNumber(double? value)
    {
        return value is { } number ? number.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
    }

    private static double? ParseOptional(string text, string path, List<ApiValidationError> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= 0)
        {
            return value;
        }

        problems.Add(new ApiValidationError(path, "not_a_number", "This is not a number that is zero or more."));
        return null;
    }

    private SensorSource BuildSource()
    {
        return SourceType switch
        {
            SensorSourceTypes.PowerShell => string.Equals(ScriptSource, PowerShellSources.File, StringComparison.Ordinal)
                ? new PowerShellSensorSource(PowerShellSources.File, Script: NullIfEmpty(Script), Engine: Engine)
                : new PowerShellSensorSource(PowerShellSources.Inline, Code: Code, Engine: Engine),
            SensorSourceTypes.ProcessRunning => new ProcessRunningSensorSource(Process.Trim()),
            SensorSourceTypes.PerfCounter => new PerfCounterSensorSource(Category.Trim(), Counter.Trim(), NullIfEmpty(Instance)),
            SensorSourceTypes.CimQuery => new CimQuerySensorSource(CimNamespace.Trim(), CimClass.Trim(), CimProperty.Trim(), NullIfEmpty(CimFilter)),
            SensorSourceTypes.ServiceState => new ServiceStateSensorSource(Service.Trim()),

            // A type that this editor cannot author (state) keeps its source as it was stored.
            _ => _original?.Source ?? new PowerShellSensorSource(PowerShellSources.Inline, Code: Code, Engine: Engine),
        };
    }
}
