using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Components.Api;

/// <summary>
/// What the pages need from an agent (PLAN.md S04, D-014, D-033): the management API as typed calls. The UI shell
/// implements it with an HTTP client; the hub UI will implement it twice (tunnelled to an agent, or against the
/// library). Every call throws <see cref="ApiException"/> when the agent answers with an error or cannot be reached;
/// the exceptions of the calls that document a <c>null</c> result are the ones that carry a different meaning.
/// </summary>
public interface IAgentApi
{
    /// <summary>Asks whether first-run setup is possible (<c>GET /auth/setup</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state, or <c>null</c> when setup is not available because a password exists.</returns>
    Task<SetupState?> GetSetupStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the first password with the setup token and signs in (<c>POST /auth/setup</c>).</summary>
    /// <param name="request">The setup token and the password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The signed-in account.</returns>
    Task<AuthMeResponse> SetUpAsync(SetupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Signs in with the password (<c>POST /auth/login</c>); the session is a cookie.</summary>
    /// <param name="request">The password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The signed-in account.</returns>
    Task<AuthMeResponse> LogInAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ends the session (<c>POST /auth/logout</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the session is ended.</returns>
    Task LogOutAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks who is signed in (<c>GET /auth/me</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account, or <c>null</c> when there is no valid session.</returns>
    Task<AuthMeResponse?> GetMeAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the agent status (<c>GET /status</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The status.</returns>
    Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the paired QoffeeLink clients (<c>GET /link/clients</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The clients.</returns>
    Task<LinkClientList> GetLinkClientsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the metric catalog with the effective settings (<c>GET /metrics</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The metrics.</returns>
    Task<MetricList> GetMetricsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the recorded values of metrics for charts (<c>GET /metrics/history</c>).</summary>
    /// <param name="metricIds">The metrics to read.</param>
    /// <param name="seconds">How far back to read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One series per metric, oldest value first.</returns>
    Task<MetricHistoryResponse> GetMetricHistoryAsync(IReadOnlyList<string> metricIds, int seconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the live metric stream (<c>GET /metrics/stream</c>, Server-Sent Events). The stream stays open, and the
    /// agent samples the metrics, until the enumeration is cancelled or disposed; callers must do so as soon as they stop
    /// showing the values (PLAN.md D-007).
    /// </summary>
    /// <param name="metricIds">The metrics to stream.</param>
    /// <param name="intervalSeconds">At most one value per this many seconds and metric.</param>
    /// <param name="cancellationToken">Closes the stream.</param>
    /// <returns>The snapshot, then the updates.</returns>
    IAsyncEnumerable<MetricStreamUpdate> StreamMetricsAsync(IReadOnlyList<string> metricIds, int intervalSeconds, CancellationToken cancellationToken = default);

    /// <summary>Lists the newest runs (<c>GET /runs</c>).</summary>
    /// <param name="limit">The page size.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The runs, newest first.</returns>
    Task<RunList> GetRunsAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Lists all commands, enabled or not (<c>GET /commands</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The commands, ordered by id.</returns>
    Task<CommandList> GetCommandsAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes <c>enabled</c> and <c>exposeToHa</c> of a command (<c>PATCH /commands/{id}</c>).</summary>
    /// <param name="id">The command id.</param>
    /// <param name="patch">The members to change; absent members stay as they are.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The command as saved.</returns>
    Task<CommandDocument> PatchCommandAsync(string id, CommandPatch patch, CancellationToken cancellationToken = default);

    /// <summary>Creates a command (<c>POST /commands</c>); answers 409 when the id exists.</summary>
    /// <param name="command">The new command.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The command as saved.</returns>
    Task<CommandDocument> CreateCommandAsync(CommandDocument command, CancellationToken cancellationToken = default);

    /// <summary>Reads one command (<c>GET /commands/{id}</c>); its <c>Revision</c> is the one to save against.</summary>
    /// <param name="id">The command id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The command; an unknown id fails with status 404.</returns>
    Task<CommandDocument> GetCommandAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a command (<c>PUT /commands/{id}</c>) if nobody saved it in the meantime: the <c>Revision</c> of the document
    /// is sent as <c>If-Match</c>, and a stale revision fails with status 412. A document without a revision replaces whatever is stored.
    /// </summary>
    /// <param name="command">The command; its id is the one in the URL.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The command as saved.</returns>
    Task<CommandDocument> ReplaceCommandAsync(CommandDocument command, CancellationToken cancellationToken = default);

    /// <summary>Validates a draft without saving it (<c>POST /commands/validate</c>).</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The report; the errors have JSON pointers into the draft.</returns>
    Task<ValidationReport> ValidateCommandAsync(CommandDocument draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and runs a draft without saving it (<c>POST /commands/test</c>). A draft that is not valid fails with
    /// status 422 and errors with the paths <c>/command/...</c>; variables that are not valid have the paths <c>/vars/&lt;name&gt;</c>.
    /// The output is read with <see cref="StreamRunAsync"/>.
    /// </summary>
    /// <param name="draft">The draft.</param>
    /// <param name="variables">The variable values by parameter name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The accepted run.</returns>
    Task<RunStarted> TestCommandAsync(CommandDocument draft, IReadOnlyDictionary<string, System.Text.Json.JsonElement> variables, CancellationToken cancellationToken = default);

    /// <summary>Lists the script files (<c>GET /scripts</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The scripts, ordered by name.</returns>
    Task<ScriptSummaryList> GetScriptsAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a command (<c>DELETE /commands/{id}</c>).</summary>
    /// <param name="id">The command id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the command is deleted.</returns>
    Task DeleteCommandAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a run (<c>POST /commands/{id}/run</c>). A request that the agent rejects because of the variables fails with
    /// status 422 and one validation error per variable, with the path <c>/vars/&lt;name&gt;</c>.
    /// </summary>
    /// <param name="commandId">The command id.</param>
    /// <param name="variables">The variable values by parameter name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The accepted run.</returns>
    Task<RunStarted> StartRunAsync(string commandId, IReadOnlyDictionary<string, System.Text.Json.JsonElement> variables, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the output of a run (<c>GET /runs/{id}/stream</c>, Server-Sent Events): the pieces of output, then one end
    /// item, after which the enumeration ends. The output of a running run is replayed from its start. Cancelling or
    /// disposing the enumeration closes the connection; the run itself is not affected.
    /// </summary>
    /// <param name="runId">The run id.</param>
    /// <param name="cancellationToken">Closes the stream.</param>
    /// <returns>The items in the order of arrival.</returns>
    IAsyncEnumerable<RunStreamItem> StreamRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Lists the recent events (<c>GET /events</c>).</summary>
    /// <param name="limit">The most events to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events, newest first.</returns>
    Task<EventList> GetEventsAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the override of a metric (<c>PUT /metrics/{id}</c>). The agent replaces the whole override of the metric, and an
    /// override without members removes it, so callers send every member that should stay in effect.
    /// </summary>
    /// <param name="id">The metric id.</param>
    /// <param name="change">The override.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>All overrides as saved.</returns>
    Task<MetricOverridesDocument> PutMetricOverrideAsync(string id, MetricOverride change, CancellationToken cancellationToken = default);

    /// <summary>Lists the custom sensors (<c>GET /sensors</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The sensors, ordered by id.</returns>
    Task<SensorList> GetSensorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a custom sensor (<c>POST /sensors</c>); answers 409 when the id exists and 422 for an invalid or unsupported sensor.</summary>
    /// <param name="sensor">The new sensor.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The sensor as saved.</returns>
    Task<SensorDocument> CreateSensorAsync(SensorDocument sensor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a custom sensor (<c>PUT /sensors/{id}</c>) if nobody saved it in the meantime: the <c>Revision</c> of the document
    /// is sent as <c>If-Match</c>, and a stale revision fails with status 412.
    /// </summary>
    /// <param name="sensor">The sensor; its id is the one in the URL.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The sensor as saved.</returns>
    Task<SensorDocument> ReplaceSensorAsync(SensorDocument sensor, CancellationToken cancellationToken = default);

    /// <summary>Deletes a custom sensor (<c>DELETE /sensors/{id}</c>).</summary>
    /// <param name="id">The sensor id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the sensor is deleted.</returns>
    Task DeleteSensorAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Samples a draft sensor once without saving it (<c>POST /sensors/test</c>); an invalid draft fails with status 422.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The parsed value or the error.</returns>
    Task<SensorTestResult> TestSensorAsync(SensorDocument draft, CancellationToken cancellationToken = default);

    /// <summary>Starts pairing (<c>POST /link/pairing</c>): creates a one-time code. Fails with status 503 when the agent has no certificate.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The code, its expiry and the certificate fingerprint.</returns>
    Task<PairingInfo> StartPairingAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the active pairing code (<c>GET /link/pairing</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The code, or <c>null</c> when there is none (it expired, was used or was cancelled).</returns>
    Task<PairingInfo?> GetPairingAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancels the active pairing code (<c>DELETE /link/pairing</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the code is cancelled.</returns>
    Task CancelPairingAsync(CancellationToken cancellationToken = default);

    /// <summary>Revokes a paired client and closes its connections (<c>DELETE /link/clients/{id}</c>); an unknown id fails with status 404.</summary>
    /// <param name="id">The client id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the client is revoked.</returns>
    Task RevokeLinkClientAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Reads the agent settings (<c>GET /settings</c>); their <c>Revision</c> is the one to save against.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The settings.</returns>
    Task<AgentSettingsDocument> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the agent settings (<c>PUT /settings</c>); the <c>Revision</c> of the document is sent as <c>If-Match</c> when it has one,
    /// and a stale revision fails with status 412.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The settings as saved.</returns>
    Task<AgentSettingsDocument> PutSettingsAsync(AgentSettingsDocument settings, CancellationToken cancellationToken = default);

    /// <summary>Changes the password (<c>POST /auth/password</c>); a wrong current password fails with status 403.</summary>
    /// <param name="request">The current and the new password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the password is changed.</returns>
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the agent to restart (<c>POST /agent/restart</c>) so that ports and the remote UI switch take effect. The agent answers
    /// 202 and goes down a moment later; the page must expect that the next call fails until the agent is back.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the agent accepted the request.</returns>
    Task RestartAgentAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists runs with filters (<c>GET /runs</c> with <c>command</c>, <c>source</c>, <c>failed</c>, <c>limit</c>, <c>offset</c>).</summary>
    /// <param name="query">The filters and the page.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The runs, newest first.</returns>
    Task<RunList> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reads one run with its variables and output (<c>GET /runs/{id}</c>).</summary>
    /// <param name="id">The run id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The run; an unknown id fails with status 404.</returns>
    Task<RunDetail> GetRunAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists events, optionally of one type (<c>GET /events</c> with <c>limit</c> and <c>type</c>).</summary>
    /// <param name="limit">The most events to return.</param>
    /// <param name="type">Only events of this type; <c>null</c> for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events, newest first.</returns>
    Task<EventList> QueryEventsAsync(int limit, string? type, CancellationToken cancellationToken = default);

    /// <summary>Reads the newest log entries (<c>GET /logs</c>); the log viewer polls it in follow mode.</summary>
    /// <param name="level">The lowest level: trace, debug, information, warning, error or critical.</param>
    /// <param name="limit">The most entries to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries, oldest first.</returns>
    Task<LogList> GetLogsAsync(string level, int limit, CancellationToken cancellationToken = default);

    /// <summary>Downloads the configuration archive (<c>GET /config/export</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The zip file.</returns>
    Task<DownloadedFile> ExportConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows what an import would do (<c>POST /config/import/preview</c>); nothing is changed.</summary>
    /// <param name="archive">The zip file.</param>
    /// <param name="includeSettings">Whether the settings would be imported as well.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The preview; an archive with invalid documents is a preview with errors, not an exception.</returns>
    Task<ConfigImportPreview> PreviewImportAsync(byte[] archive, bool includeSettings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the archive (<c>POST /config/import</c>). The agent validates every document first; when any is invalid it fails with
    /// status 422 and applies nothing.
    /// </summary>
    /// <param name="archive">The zip file.</param>
    /// <param name="includeSettings">Whether the settings are imported as well.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was applied.</returns>
    Task<ConfigImportResult> ImportConfigAsync(byte[] archive, bool includeSettings, CancellationToken cancellationToken = default);

    /// <summary>Lists the kept revisions of a document (<c>GET /config/revisions/{kind}/{id}</c>).</summary>
    /// <param name="kind">One of <see cref="RevisionKinds"/>.</param>
    /// <param name="id">The document id; <c>settings</c> for the settings.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revisions, newest first.</returns>
    Task<RevisionList> GetRevisionsAsync(string kind, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores an earlier revision as a new revision (<c>POST /config/revisions/{kind}/{id}/{revision}/restore</c>); the stored revision
    /// is sent as <c>If-Match</c> when given.
    /// </summary>
    /// <param name="kind">One of <see cref="RevisionKinds"/>.</param>
    /// <param name="id">The document id.</param>
    /// <param name="revision">The revision to restore.</param>
    /// <param name="expectedRevision">The revision that is stored now, or <c>null</c> to skip the check.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the revision is restored.</returns>
    Task RestoreRevisionAsync(string kind, string id, int revision, int? expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Downloads the diagnostics bundle (<c>GET /diagnostics</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The zip file.</returns>
    Task<DownloadedFile> DownloadDiagnosticsAsync(CancellationToken cancellationToken = default);
}
