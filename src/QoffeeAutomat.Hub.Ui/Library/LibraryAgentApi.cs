using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tunnel;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Hub.Ui.Library;

/// <summary>
/// <see cref="IAgentApi"/> over the library of the hub (PLAN.md S20, D-033, T916): the command editor and the sensor editor of the component library
/// are written once, and here they read and write the documents of the hub library (<c>/api/v1/library</c>) instead of the documents of one agent.
/// <list type="bullet">
/// <item>Commands, sensors and script files are library documents. A document is stored with the origin <c>hub</c> (that is what an agent shows as
/// read-only), so the origin is removed when a document is handed to an editor, which would otherwise refuse to change it; the hub sets it again.</item>
/// <item>Validation is the hub's: a save answers 422 with the errors that the editor shows at the fields. The editor's live check has nothing to ask
/// (the hub has no validate endpoint) and reports a draft that is valid.</item>
/// <item>Running is not what the library does. What the test panel runs, and what looks at a real PC (the platform, the metrics), goes through the tunnel
/// to the agent that the user chose in <see cref="AgentSelection.TestTargetId"/>; without one the call fails with the code <see cref="NoTestTargetCode"/>
/// and the panel shows why.</item>
/// <item>Everything else (history, link, settings, secrets, modes, backup) is not part of the library and answers 404 <c>library_unsupported</c>, which
/// the editors treat like an agent without the feature.</item>
/// </list>
/// The sign-in is the hub's, as in the tunnel.
/// </summary>
public sealed class LibraryAgentApi : IAgentApi
{
    /// <summary>The error code of a test that needs an agent and has none chosen.</summary>
    public const string NoTestTargetCode = "no_test_target";

    /// <summary>The error code of a call that the library does not offer.</summary>
    public const string UnsupportedCode = "library_unsupported";

    private readonly IHubApi _hub;
    private readonly AgentSelection _selection;

    // The tunnel reads the agent it talks to from a selection; the library has its own so that choosing a test target never changes the selection of
    // the agent pages.
    private readonly AgentSelection _target = new();
    private readonly TunnelAgentApi _tunnel;

    /// <summary>Creates the API.</summary>
    /// <param name="hub">The hub API: the library and the session.</param>
    /// <param name="http">The HTTP client of the tunnel; its base address is the base address of the page.</param>
    /// <param name="selection">The selection that holds the test target.</param>
    public LibraryAgentApi(IHubApi hub, HttpClient http, AgentSelection selection)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(selection);

        _hub = hub;
        _selection = selection;
        _tunnel = new TunnelAgentApi(http, _target, hub);
    }

    // The library cannot be told apart from the tunnel for the sign-in ---------------------------------------------------------------

    /// <inheritdoc />
    public Task<SetupState?> GetSetupStateAsync(CancellationToken cancellationToken = default) => _tunnel.GetSetupStateAsync(cancellationToken);

    /// <inheritdoc />
    public Task<AuthMeResponse> SetUpAsync(SetupRequest request, CancellationToken cancellationToken = default) => _tunnel.SetUpAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<AuthMeResponse> LogInAsync(LoginRequest request, CancellationToken cancellationToken = default) => _tunnel.LogInAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task LogOutAsync(CancellationToken cancellationToken = default) => _tunnel.LogOutAsync(cancellationToken);

    /// <inheritdoc />
    public Task<AuthMeResponse?> GetMeAsync(CancellationToken cancellationToken = default) => _tunnel.GetMeAsync(cancellationToken);

    // Looks at the test target ------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Tunnel().GetStatusAsync(cancellationToken);

    /// <inheritdoc />
    public Task<MetricList> GetMetricsAsync(CancellationToken cancellationToken = default) => Tunnel().GetMetricsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<RunStarted> TestCommandAsync(CommandDocument draft, IReadOnlyDictionary<string, JsonElement> variables, CancellationToken cancellationToken = default)
    {
        return Tunnel().TestCommandAsync(draft, variables, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RunStreamItem> StreamRunAsync(string runId, CancellationToken cancellationToken = default) => Tunnel().StreamRunAsync(runId, cancellationToken);

    /// <inheritdoc />
    public Task<SensorTestResult> TestSensorAsync(SensorDocument draft, CancellationToken cancellationToken = default) => Tunnel().TestSensorAsync(draft, cancellationToken);

    // Commands ----------------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<CommandList> GetCommandsAsync(CancellationToken cancellationToken = default)
    {
        LibraryDocumentList list = await _hub.GetLibraryAsync(DocumentKinds.Command, cancellationToken);
        CommandDocument[] commands = await Task.WhenAll(list.Documents.Select(summary => GetCommandAsync(summary.Id, cancellationToken)));
        return new CommandList(commands);
    }

    /// <inheritdoc />
    public async Task<CommandDocument> GetCommandAsync(string id, CancellationToken cancellationToken = default)
    {
        LibraryDocumentInfo info = await _hub.GetLibraryDocumentAsync(DocumentKinds.Command, id, null, cancellationToken);
        return ToCommand(info);
    }

    /// <inheritdoc />
    public async Task<CommandDocument> CreateCommandAsync(CommandDocument command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await EnsureNewAsync(DocumentKinds.Command, command.Id, cancellationToken);
        return await ReplaceCommandAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CommandDocument> ReplaceCommandAsync(CommandDocument command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        JsonElement body = JsonSerializer.SerializeToElement(command, ApiDocumentJsonContext.Default.CommandDocument);
        return ToCommand(await _hub.SaveLibraryDocumentAsync(DocumentKinds.Command, command.Id, body, cancellationToken));
    }

    /// <inheritdoc />
    public Task<ValidationReport> ValidateCommandAsync(CommandDocument draft, CancellationToken cancellationToken = default)
    {
        // The hub validates on save (422); there is no check to ask in between.
        return Task.FromResult(new ValidationReport(true, []));
    }

    /// <inheritdoc />
    public Task DeleteCommandAsync(string id, CancellationToken cancellationToken = default)
    {
        return _hub.DeleteLibraryDocumentAsync(DocumentKinds.Command, id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ScriptSummaryList> GetScriptsAsync(CancellationToken cancellationToken = default)
    {
        LibraryDocumentList list = await _hub.GetLibraryAsync(DocumentKinds.Script, cancellationToken);
        return new ScriptSummaryList([.. list.Documents.Select(document => new ScriptSummary(document.Id, document.Hash, document.UpdatedAt))]);
    }

    // Sensors -----------------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<SensorList> GetSensorsAsync(CancellationToken cancellationToken = default)
    {
        LibraryDocumentList list = await _hub.GetLibraryAsync(DocumentKinds.Sensor, cancellationToken);
        SensorDocument[] sensors = await Task.WhenAll(list.Documents.Select(async summary =>
            ToSensor(await _hub.GetLibraryDocumentAsync(DocumentKinds.Sensor, summary.Id, null, cancellationToken))));
        return new SensorList(sensors);
    }

    /// <inheritdoc />
    public async Task<SensorDocument> CreateSensorAsync(SensorDocument sensor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        await EnsureNewAsync(DocumentKinds.Sensor, sensor.Id, cancellationToken);
        return await ReplaceSensorAsync(sensor, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SensorDocument> ReplaceSensorAsync(SensorDocument sensor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        JsonElement body = JsonSerializer.SerializeToElement(sensor, ApiDocumentJsonContext.Default.SensorDocument);
        return ToSensor(await _hub.SaveLibraryDocumentAsync(DocumentKinds.Sensor, sensor.Id, body, cancellationToken));
    }

    /// <inheritdoc />
    public Task DeleteSensorAsync(string id, CancellationToken cancellationToken = default)
    {
        return _hub.DeleteLibraryDocumentAsync(DocumentKinds.Sensor, id, cancellationToken);
    }

    // Not the library's ---------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<HubConfigState> GetHubConfigAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<HotkeyStatusResponse> GetHotkeyStatusAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<HardwareSensorStatus> GetHardwareSensorStatusAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<LinkClientList> GetLinkClientsAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<MetricHistoryResponse> GetMetricHistoryAsync(IReadOnlyList<string> metricIds, int seconds, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public IAsyncEnumerable<MetricStreamUpdate> StreamMetricsAsync(IReadOnlyList<string> metricIds, int intervalSeconds, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<RunList> GetRunsAsync(int limit, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<CommandDocument> PatchCommandAsync(string id, CommandPatch patch, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<RunStarted> StartRunAsync(string commandId, IReadOnlyDictionary<string, JsonElement> variables, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<EventList> GetEventsAsync(int limit, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<MetricOverridesDocument> PutMetricOverrideAsync(string id, MetricOverride change, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<SensorValue> GetSensorValueAsync(string id, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<SensorValue> SetSensorValueAsync(string id, string value, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<ModeGroupList> GetModeGroupsAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<ModeGroupDocument> CreateModeGroupAsync(ModeGroupDocument group, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<ModeGroupDocument> ReplaceModeGroupAsync(ModeGroupDocument group, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task DeleteModeGroupAsync(string id, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<ModeState> GetModeStateAsync(string id, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<ModeSwitchStarted> SwitchModeAsync(string id, string mode, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<TemplateList> GetTemplatesAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<TemplateDetail> GetTemplateAsync(string id, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<TemplateInstantiation> InstantiateTemplateAsync(string id, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<SecretList> GetSecretsAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<SecretSummary> PutSecretAsync(string name, string value, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<PairingInfo> StartPairingAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<PairingInfo?> GetPairingAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task CancelPairingAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task RevokeLinkClientAsync(string id, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<AgentSettingsDocument> GetSettingsAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<AgentSettingsDocument> PutSettingsAsync(AgentSettingsDocument settings, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task RestartAgentAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<RunList> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<RunDetail> GetRunAsync(string id, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<EventList> QueryEventsAsync(int limit, string? type, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<LogList> GetLogsAsync(string level, int limit, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<DownloadedFile> ExportConfigAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<ConfigImportPreview> PreviewImportAsync(byte[] archive, bool includeSettings, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<ConfigImportResult> ImportConfigAsync(byte[] archive, bool includeSettings, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<RevisionList> GetRevisionsAsync(string kind, string id, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task RestoreRevisionAsync(string kind, string id, int revision, int? expectedRevision, CancellationToken cancellationToken = default) => throw Unsupported();

    /// <inheritdoc />
    public Task<DownloadedFile> DownloadDiagnosticsAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    // Helpers -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>Turns the body of a library command into the document that the editor shows: the revision of the library, and no origin.</summary>
    /// <param name="info">The library document.</param>
    /// <returns>The command.</returns>
    public static CommandDocument ToCommand(LibraryDocumentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        CommandDocument document = Read(info, ApiDocumentJsonContext.Default.CommandDocument);
        return document with { Revision = info.Revision, Origin = null };
    }

    /// <summary>Turns the body of a library sensor into the document that the editor shows: the revision of the library, and no origin.</summary>
    /// <param name="info">The library document.</param>
    /// <returns>The sensor.</returns>
    public static SensorDocument ToSensor(LibraryDocumentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        SensorDocument document = Read(info, ApiDocumentJsonContext.Default.SensorDocument);
        return document with { Revision = info.Revision, Origin = null };
    }

    private static T Read<T>(LibraryDocumentInfo info, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return info.Body.Deserialize(typeInfo)
                ?? throw Invalid(info, null);
        }
        catch (JsonException exception)
        {
            throw Invalid(info, exception);
        }
    }

    private static ApiException Invalid(LibraryDocumentInfo info, Exception? cause)
    {
        var problem = new ApiProblem("about:blank", "Invalid document", 502, "The library document " + info.Kind + "/" + info.Id + " cannot be read.", "invalid_response");
        return new ApiException(502, problem, innerException: cause);
    }

    private async Task EnsureNewAsync(string kind, string id, CancellationToken cancellationToken)
    {
        // The hub's PUT creates or replaces; the editor's "create" must not replace a document of the same id by accident.
        try
        {
            await _hub.GetLibraryDocumentAsync(kind, id, null, cancellationToken);
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            return;
        }

        var problem = new ApiProblem("about:blank", "Conflict", 409, "A library document with this id exists already.", "already_exists");
        throw new ApiException(409, problem);
    }

    private TunnelAgentApi Tunnel()
    {
        string id = _selection.TestTargetId ?? throw NoTarget();
        _target.Select(id);
        return _tunnel;
    }

    private static ApiException NoTarget()
    {
        var problem = new ApiProblem("about:blank", "No test target", 409, "Choose an online PC to run the draft on.", NoTestTargetCode);
        return new ApiException(409, problem);
    }

    private static ApiException Unsupported()
    {
        var problem = new ApiProblem("about:blank", "Not available", 404, "The library does not offer this.", UnsupportedCode);
        return new ApiException(404, problem);
    }
}
