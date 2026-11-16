using System.Text.Json;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Contracts.Api;

/// <summary>Body of <c>POST /api/v1/commands/{id}/run</c>.</summary>
/// <param name="Vars">Variable values by parameter name; the agent validates them against the command's parameters.</param>
public sealed record RunStartRequest(IReadOnlyDictionary<string, JsonElement>? Vars = null);

/// <summary>Body of <c>POST /api/v1/commands/test</c>: a draft document and the variables to run it with.</summary>
/// <param name="Command">The draft; it is validated like a save, but not stored.</param>
/// <param name="Vars">Variable values by parameter name.</param>
public sealed record CommandTestRequest(CommandDocument Command, IReadOnlyDictionary<string, JsonElement>? Vars = null);

/// <summary>Response of a run that was accepted (HTTP 202).</summary>
/// <param name="RunId">The id of the run; use it for <c>/runs/{id}</c> and <c>/runs/{id}/stream</c>.</param>
/// <param name="Command">The command id.</param>
/// <param name="Source">Where the run came from: <c>ui</c> or <c>test</c>.</param>
public sealed record RunStarted(string RunId, string Command, string Source);

/// <summary>A run in a listing (<c>GET /api/v1/runs</c>).</summary>
/// <param name="Id">The run id.</param>
/// <param name="Command">The command id.</param>
/// <param name="Source">Where the run came from: <c>ha</c>, <c>ui</c>, <c>hub</c>, <c>cli</c>, <c>sequence</c> or <c>test</c>.</param>
/// <param name="State"><c>waiting</c>, <c>running</c> or <c>finished</c>.</param>
/// <param name="StartedAt">When the run started (or was rejected).</param>
/// <param name="DurationMs">How long the run took in milliseconds; zero for a rejected request.</param>
/// <param name="ExitCode">The exit code; absent when nothing ran.</param>
/// <param name="ErrorCode">The QoffeeLink error code when the run failed before or instead of producing an exit code.</param>
/// <param name="Succeeded">True when the run ended with exit code zero and no error code.</param>
/// <param name="ClientId">The link client or session that asked.</param>
/// <param name="ParentRunId">The sequence run that started this run.</param>
public sealed record RunSummary(
    string Id,
    string Command,
    string Source,
    string State,
    DateTimeOffset StartedAt,
    long DurationMs,
    int? ExitCode,
    string? ErrorCode,
    bool Succeeded,
    string? ClientId,
    string? ParentRunId);

/// <summary>One run with its variables and output (<c>GET /api/v1/runs/{id}</c>). Values of secret variables are redacted.</summary>
/// <param name="Id">The run id.</param>
/// <param name="Command">The command id.</param>
/// <param name="Source">Where the run came from.</param>
/// <param name="State"><c>waiting</c>, <c>running</c> or <c>finished</c>.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="DurationMs">How long the run took in milliseconds.</param>
/// <param name="ExitCode">The exit code.</param>
/// <param name="ErrorCode">The error code.</param>
/// <param name="Succeeded">True when the run succeeded.</param>
/// <param name="ClientId">The link client or session that asked.</param>
/// <param name="UserId">The Home Assistant user on whose behalf the command ran.</param>
/// <param name="ParentRunId">The sequence run that started this run.</param>
/// <param name="Variables">The variables as a JSON object.</param>
/// <param name="StandardOutput">The standard output (empty while the run is active; use the stream).</param>
/// <param name="StandardError">The standard error, or the error message of a rejected request.</param>
/// <param name="OutputTruncated">The output was cut.</param>
public sealed record RunDetail(
    string Id,
    string Command,
    string Source,
    string State,
    DateTimeOffset StartedAt,
    long DurationMs,
    int? ExitCode,
    string? ErrorCode,
    bool Succeeded,
    string? ClientId,
    string? UserId,
    string? ParentRunId,
    JsonElement? Variables,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated);

/// <summary>Response of <c>GET /api/v1/runs</c>.</summary>
/// <param name="Runs">The runs, newest first.</param>
/// <param name="Limit">The page size that was applied.</param>
/// <param name="Offset">How many of the newest matching runs were skipped.</param>
/// <param name="HasMore">True when another page follows.</param>
public sealed record RunList(IReadOnlyList<RunSummary> Runs, int Limit, int Offset, bool HasMore);

/// <summary>Data of the event <c>output</c> of <c>GET /api/v1/runs/{id}/stream</c>.</summary>
/// <param name="Stream"><c>stdout</c> or <c>stderr</c>.</param>
/// <param name="Text">The text; a piece of output is not necessarily a whole line.</param>
/// <param name="Timestamp">When the piece was read.</param>
public sealed record RunOutputEvent(string Stream, string Text, DateTimeOffset Timestamp);

/// <summary>Data of the last event <c>end</c> of <c>GET /api/v1/runs/{id}/stream</c>.</summary>
/// <param name="RunId">The run id.</param>
/// <param name="Succeeded">True when the run succeeded.</param>
/// <param name="ExitCode">The exit code.</param>
/// <param name="ErrorCode">The error code.</param>
/// <param name="ErrorMessage">The explanation of the error code.</param>
/// <param name="DurationMs">How long the run took in milliseconds.</param>
/// <param name="OutputTruncated">The output was cut.</param>
public sealed record RunEndEvent(
    string RunId,
    bool Succeeded,
    int? ExitCode,
    string? ErrorCode,
    string? ErrorMessage,
    long DurationMs,
    bool OutputTruncated);

/// <summary>Data of the events <c>snapshot</c> and <c>update</c> of <c>GET /api/v1/metrics/stream</c>.</summary>
/// <param name="Timestamp">When the update was produced.</param>
/// <param name="Snapshot">True when the update carries every subscribed metric.</param>
/// <param name="Values">Metric id to value; <c>null</c> means unavailable.</param>
public sealed record MetricStreamUpdate(DateTimeOffset Timestamp, bool Snapshot, IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>One recorded value of a metric.</summary>
/// <param name="Timestamp">When the value was sampled.</param>
/// <param name="Value">The value; absent means unavailable.</param>
public sealed record MetricPoint(DateTimeOffset Timestamp, JsonElement? Value);

/// <summary>The recorded values of one metric, oldest first.</summary>
/// <param name="Id">The metric id.</param>
/// <param name="Points">The values.</param>
public sealed record MetricSeries(string Id, IReadOnlyList<MetricPoint> Points);

/// <summary>Response of <c>GET /api/v1/metrics/history</c>.</summary>
/// <param name="Series">One series per requested metric.</param>
public sealed record MetricHistoryResponse(IReadOnlyList<MetricSeries> Series);

/// <summary>An event in <c>GET /api/v1/events</c>.</summary>
/// <param name="Id">The id in the recent-events table.</param>
/// <param name="Type">The event type.</param>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Data">The data of the event.</param>
public sealed record EventInfo(long Id, string Type, DateTimeOffset Timestamp, JsonElement? Data);

/// <summary>Response of <c>GET /api/v1/events</c>.</summary>
/// <param name="Events">The events, newest first.</param>
public sealed record EventList(IReadOnlyList<EventInfo> Events);

/// <summary>A line of the agent log.</summary>
/// <param name="Timestamp">When it was logged.</param>
/// <param name="Level">The log level, for example <c>Warning</c>.</param>
/// <param name="Category">The logger category.</param>
/// <param name="Message">The message.</param>
/// <param name="Exception">The exception text, when there was one.</param>
public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Category, string Message, string? Exception);

/// <summary>Response of <c>GET /api/v1/logs</c>.</summary>
/// <param name="Entries">The newest entries, oldest first.</param>
public sealed record LogList(IReadOnlyList<LogEntry> Entries);

/// <summary>The file <c>export.json</c> of a configuration archive.</summary>
/// <param name="Format">The version of the archive layout.</param>
/// <param name="Product">Always <c>QoffeeAutomat</c>.</param>
/// <param name="AgentVersion">The version of the agent that wrote the archive.</param>
/// <param name="ExportedAt">When the archive was written.</param>
/// <param name="Commands">The number of commands.</param>
/// <param name="Sensors">The number of sensors.</param>
/// <param name="Scripts">The number of scripts.</param>
public sealed record ConfigExportInfo(int Format, string Product, string AgentVersion, DateTimeOffset ExportedAt, int Commands, int Sensors, int Scripts);

/// <summary>Response of <c>POST /api/v1/config/import</c>: what was applied.</summary>
/// <param name="Commands">The number of commands that were stored.</param>
/// <param name="Sensors">The number of sensors that were stored.</param>
/// <param name="Scripts">The number of scripts that were stored.</param>
/// <param name="Metrics">Whether the metric overrides were stored.</param>
/// <param name="Settings">Whether the settings were stored.</param>
public sealed record ConfigImportResult(int Commands, int Sensors, int Scripts, bool Metrics, bool Settings);

/// <summary>A configuration file that is skipped, in the diagnostics bundle (the path is reduced to the file name).</summary>
/// <param name="File">The file name.</param>
/// <param name="Code">The problem code.</param>
/// <param name="Message">The explanation.</param>
public sealed record DiagnosticsProblem(string File, string Code, string Message);

/// <summary>The file <c>diagnostics.json</c> of the diagnostics bundle: versions, platform and counters, no secrets.</summary>
/// <param name="GeneratedAt">When the bundle was made.</param>
/// <param name="AgentVersion">The agent version.</param>
/// <param name="Runtime">The .NET runtime.</param>
/// <param name="OperatingSystem">The operating system description.</param>
/// <param name="Platform">The selected platform.</param>
/// <param name="UptimeSeconds">How long the agent has been running.</param>
/// <param name="Commands">The number of commands.</param>
/// <param name="Sensors">The number of sensors.</param>
/// <param name="Scripts">The number of scripts.</param>
/// <param name="PairedClients">The number of paired clients.</param>
/// <param name="ManifestRev">The revision of the manifest.</param>
/// <param name="ConfigProblems">The configuration files that are skipped.</param>
public sealed record DiagnosticsInfo(
    DateTimeOffset GeneratedAt,
    string AgentVersion,
    string Runtime,
    string OperatingSystem,
    string Platform,
    long UptimeSeconds,
    int Commands,
    int Sensors,
    int Scripts,
    int PairedClients,
    string ManifestRev,
    IReadOnlyList<DiagnosticsProblem> ConfigProblems);
