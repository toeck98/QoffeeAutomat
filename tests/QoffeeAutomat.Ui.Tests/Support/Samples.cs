using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Tests.Support;

/// <summary>Data that the fake agent answers with.</summary>
internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public static JsonElement Number(double value)
    {
        using JsonDocument document = JsonDocument.Parse(value.ToString("R", CultureInfo.InvariantCulture));
        return document.RootElement.Clone();
    }

    public static JsonElement Null()
    {
        using JsonDocument document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    public static JsonElement Text(string value)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    public static AgentStatus Status(
        string version = "1.2.3",
        bool paused = false,
        long uptimeSeconds = (3 * 86400) + (4 * 3600) + (12 * 60),
        bool sessionReady = true,
        int configProblems = 0,
        string platform = "windows")
    {
        return new AgentStatus(
            version,
            "agent-1",
            "Study PC",
            Now.AddSeconds(-uptimeSeconds),
            uptimeSeconds,
            new PlatformStatus(platform, platform == "windows" ? "Windows" : "Linux", "11", "x64", "STUDY-PC"),
            new SessionStatus(sessionReady, "alice", Locked: false, UserIsAdministrator: true),
            new LinkStatus(8481, PairedClients: 2, Connections: 1),
            paused,
            ActiveRuns: 1,
            ManifestRev: "abc",
            configProblems);
    }

    public static LinkClientList Clients()
    {
        LinkClientInfo[] clients =
        [
            new LinkClientInfo("c1", "Home Assistant", "homeassistant", Now.AddDays(-10), Now.AddSeconds(-5), Connections: 1),
            new LinkClientInfo("c2", "Test tool", "tool", Now.AddDays(-3), null, Connections: 0),
        ];
        return new LinkClientList(clients);
    }

    public static MetricInfo Metric(
        string id,
        string name,
        string unit = "%",
        bool enabled = true,
        string valueType = "number",
        double? value = null,
        int? precision = 1,
        string? entityCategory = null)
    {
        return new MetricInfo(
            id,
            name,
            "sensor",
            valueType,
            "system",
            unit,
            null,
            null,
            entityCategory,
            null,
            precision,
            null,
            true,
            enabled,
            true,
            2,
            null,
            false,
            value is not null,
            value is { } number ? Number(number) : null);
    }

    public static MetricList Catalog()
    {
        MetricInfo[] metrics =
        [
            Metric("cpu.usage", "CPU usage", value: 12.5),
            Metric("memory.used_percent", "Memory used", value: 40),
            Metric("system.uptime", "Uptime", "s", entityCategory: "diagnostic"),
            Metric("gpu.usage", "GPU usage", enabled: false),
        ];
        return new MetricList(metrics);
    }

    public static MetricInfo CustomMetric(string sensorId, string name, double? value = null)
    {
        return Metric("custom." + sensorId, name, value: value) with { Custom = true, Context = null };
    }

    public static SensorDocument Sensor(
        string id,
        string? name = null,
        SensorSource? source = null,
        string valueType = "number",
        string kind = "sensor",
        string? unit = null,
        string? deviceClass = null,
        string? stateClass = null,
        bool enabled = true,
        bool exposeToHa = true)
    {
        return new SensorDocument(
            DocumentSchema.CurrentVersion,
            id,
            name ?? id,
            enabled,
            exposeToHa,
            kind,
            valueType,
            source ?? new PowerShellSensorSource(PowerShellSources.Inline, Code: "Get-Date"),
            Unit: unit,
            DeviceClass: deviceClass,
            StateClass: stateClass,
            Revision: 1,
            UpdatedAt: Now,
            Origin: DocumentOrigins.Local);
    }

    public static MetricHistoryResponse History()
    {
        MetricPoint[] cpu = [Point(-120, 10), Point(-60, 11), Point(-1, 12.5)];
        MetricPoint[] memory = [Point(-120, 39), Point(-60, 40)];
        MetricSeries[] series = [new MetricSeries("cpu.usage", cpu), new MetricSeries("memory.used_percent", memory)];
        return new MetricHistoryResponse(series);
    }

    public static MetricPoint Point(int secondsAgo, double value) => new(Now.AddSeconds(secondsAgo), Number(value));

    public static RunList Runs()
    {
        RunSummary[] runs =
        [
            new RunSummary("r2", "lock_screen", "ha", "finished", Now.AddMinutes(-1), 85, 0, null, true, "c1", null),
            new RunSummary("r1", "backup", "ui", "finished", Now.AddMinutes(-9), 4200, 1, "exit_nonzero", false, null, null),
        ];
        return new RunList(runs, Limit: 8, Offset: 0, HasMore: false);
    }

    public static EventList Events()
    {
        EventInfo[] events =
        [
            new EventInfo(2, "session.locked", Now.AddMinutes(-2), null),
            new EventInfo(1, "custom", Now.AddMinutes(-20), Text("hello")),
        ];
        return new EventList(events);
    }

    public static MetricStreamUpdate Update(DateTimeOffset timestamp, params (string Id, double Value)[] values)
    {
        return new MetricStreamUpdate(timestamp, Snapshot: false, values.ToDictionary(item => item.Id, item => Number(item.Value), StringComparer.Ordinal));
    }

    public static CommandDocument Command(
        string id,
        string? name = null,
        bool enabled = true,
        bool exposeToHa = true,
        string runAs = RunAsValues.User,
        CommandAction? action = null,
        string? description = null,
        params ParameterDefinition[] parameters)
    {
        return new CommandDocument(
            DocumentSchema.CurrentVersion,
            id,
            name ?? id,
            enabled,
            exposeToHa,
            runAs,
            action ?? new BuiltinAction("session.lock"),
            description,
            Parameters: parameters.Length == 0 ? null : parameters,
            TimeoutSeconds: 60,
            Revision: 1,
            UpdatedAt: Now,
            Origin: DocumentOrigins.Local);
    }

    public static List<CommandDocument> Commands()
    {
        return
        [
            Command("backup", "Back up documents", enabled: false, exposeToHa: false, runAs: RunAsValues.System, action: new PowerShellAction(PowerShellSources.Inline, Code: "param($Target)"), description: "Copies the documents to the target."),
            Command("lock_screen", "Lock screen", description: "Locks the session."),
            Command("night_mode", "Night mode", exposeToHa: false, action: new SequenceAction([new SequenceStep(Command: "lock_screen")])),
            Command("open_notes", "Open notes", runAs: RunAsValues.UserElevated, action: new ProcessAction("notepad.exe")),
        ];
    }

    public static ParameterDefinition TextParameter(string name, bool required = false, int? maxLength = null, string? pattern = null, bool secret = false)
    {
        return new ParameterDefinition(name, ParameterTypes.Text, required, MaxLength: maxLength, Pattern: pattern, Secret: secret);
    }

    public static ParameterDefinition WholeParameter(string name, bool required = false, double? min = null, double? max = null, double? step = null, double? defaultValue = null)
    {
        return new ParameterDefinition(
            name,
            ParameterTypes.WholeNumber,
            required,
            defaultValue is { } value ? Number(value) : null,
            min,
            max,
            step);
    }

    public static ParameterDefinition ChoiceParameter(string name, bool required = false, params string[] options)
    {
        return new ParameterDefinition(name, ParameterTypes.Choice, required, Options: options);
    }

    public static RunDetail RunDetail(string id)
    {
        bool failed = id == "r1";
        using JsonDocument variables = JsonDocument.Parse("{\"minutes\":5,\"password\":\"***\"}");
        return new RunDetail(
            id,
            failed ? "backup" : "lock_screen",
            failed ? "ui" : "ha",
            "finished",
            Now.AddMinutes(failed ? -9 : -1),
            failed ? 4200 : 85,
            failed ? 1 : 0,
            failed ? "exit_nonzero" : null,
            !failed,
            failed ? null : "c1",
            failed ? null : "ha-user",
            null,
            variables.RootElement.Clone(),
            failed ? "copying files" : "locked",
            failed ? "disk full" : string.Empty,
            OutputTruncated: false);
    }

    public static List<LogEntry> Logs()
    {
        return
        [
            new LogEntry(Now.AddSeconds(-30), "Debug", "Engine", "sampled cpu", null),
            new LogEntry(Now.AddSeconds(-20), "Information", "Agent", "agent started", null),
            new LogEntry(Now.AddSeconds(-10), "Warning", "Link", "client lost", null),
            new LogEntry(Now.AddSeconds(-5), "Error", "Run", "run failed", "System.InvalidOperationException: boom"),
        ];
    }

    /// <summary>The order of the log levels, so that the fake can filter like the agent.</summary>
    public static int LogRank(string level)
    {
        return level.ToLowerInvariant() switch
        {
            "trace" => 0,
            "debug" => 1,
            "information" => 2,
            "warning" => 3,
            "error" => 4,
            "critical" => 5,
            _ => 2,
        };
    }

    public static ConfigImportPreview ImportPreview()
    {
        return new ConfigImportPreview(
            true,
            [
                new ImportPreviewItem("command", "lock_screen", "changed"),
                new ImportPreviewItem("command", "new_one", "new"),
                new ImportPreviewItem("sensor", "temp", "unchanged"),
            ],
            []);
    }

    public static ConfigImportPreview InvalidImportPreview()
    {
        return new ConfigImportPreview(
            false,
            [new ImportPreviewItem("command", "lock_screen", "changed")],
            [new ApiValidationError("/config/commands/broken.json/steps/0", "required", "The step needs an action.")]);
    }

    public static RevisionList RevisionsOf(string kind, string id, int current)
    {
        RevisionEntry[] revisions = [.. Enumerable.Range(1, current).Reverse().Select(number => new RevisionEntry(number, Now.AddDays(-(current - number))))];
        return new RevisionList(kind, id, current, revisions);
    }

    public static ApiProblem Problem(int status, string code, string detail, params ApiValidationError[] errors)
    {
        return new ApiProblem("about:blank", "Problem", status, detail, code, errors.Length == 0 ? null : errors);
    }
}
