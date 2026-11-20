using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Ui;

/// <summary>
/// <see cref="IAgentApi"/> over a typed <see cref="HttpClient"/> (PLAN.md S08, S09 item 1, D-014). It talks to the agent
/// that served the UI, so the session cookie travels with every request; requests that change state carry the
/// anti-forgery header; bodies are read and written with the source-generated <see cref="ApiJsonContext"/> (no
/// reflection); and every error answer becomes an <see cref="ApiException"/> that carries the problem details.
/// </summary>
public class AgentApiClient : IAgentApi
{
    private const string DefaultApiBase = "api/v1/";
    private const string EventStreamMediaType = "text/event-stream";

    private readonly HttpClient _http;

    /// <summary>Creates the client.</summary>
    /// <param name="http">The HTTP client; its base address is the address of the agent.</param>
    public AgentApiClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);

        _http = http;
    }

    /// <summary>
    /// The path below the base address that every call starts with; it ends with a slash. A derived client that reaches the
    /// management API of an agent by another route (the tunnel of the hub, PLAN.md S20) overrides it.
    /// </summary>
    protected virtual string ApiBase => DefaultApiBase;

    /// <inheritdoc />
    public virtual async Task<SetupState?> GetSetupStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync("auth/setup", ApiJsonContext.Default.SetupState, cancellationToken);
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public virtual Task<AuthMeResponse> SetUpAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PostAsync("auth/setup", request, ApiJsonContext.Default.SetupRequest, ApiJsonContext.Default.AuthMeResponse, cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<AuthMeResponse> LogInAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PostAsync("auth/login", request, ApiJsonContext.Default.LoginRequest, ApiJsonContext.Default.AuthMeResponse, cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "auth/logout", null, HttpCompletionOption.ResponseContentRead, streaming: false, cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<AuthMeResponse?> GetMeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync("auth/me", ApiJsonContext.Default.AuthMeResponse, cancellationToken);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("status", ApiJsonContext.Default.AgentStatus, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HubConfigState> GetHubConfigAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("hub/config", ApiJsonContext.Default.HubConfigState, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HotkeyStatusResponse> GetHotkeyStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("hotkeys", ApiJsonContext.Default.HotkeyStatusResponse, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HardwareSensorStatus> GetHardwareSensorStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("hardware-sensors", ApiJsonContext.Default.HardwareSensorStatus, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LinkClientList> GetLinkClientsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("link/clients", ApiJsonContext.Default.LinkClientList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MetricList> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("metrics", ApiJsonContext.Default.MetricList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MetricHistoryResponse> GetMetricHistoryAsync(IReadOnlyList<string> metricIds, int seconds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        string path = "metrics/history?metrics=" + Uri.EscapeDataString(string.Join(',', metricIds))
            + "&seconds=" + seconds.ToString(CultureInfo.InvariantCulture);
        return GetAsync(path, ApiJsonContext.Default.MetricHistoryResponse, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MetricStreamUpdate> StreamMetricsAsync(
        IReadOnlyList<string> metricIds,
        int intervalSeconds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        string path = "metrics/stream?metrics=" + Uri.EscapeDataString(string.Join(',', metricIds))
            + "&interval=" + intervalSeconds.ToString(CultureInfo.InvariantCulture);

        await foreach (ServerSentEvent item in ReadEventsAsync(path, cancellationToken))
        {
            if (item.Name is not ("snapshot" or "update"))
            {
                continue;
            }

            MetricStreamUpdate? update = ParseEvent(item.Data, ApiJsonContext.Default.MetricStreamUpdate);
            if (update is not null)
            {
                yield return update;
            }
        }
    }

    /// <inheritdoc />
    public Task<CommandList> GetCommandsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("commands", ApiDocumentJsonContext.Default.CommandList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CommandDocument> PatchCommandAsync(string id, CommandPatch patch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(patch);

        return SendJsonAsync(
            HttpMethod.Patch,
            "commands/" + Uri.EscapeDataString(id),
            patch,
            ApiJsonContext.Default.CommandPatch,
            ApiDocumentJsonContext.Default.CommandDocument,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CommandDocument> CreateCommandAsync(CommandDocument command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return PostAsync("commands", command, ApiDocumentJsonContext.Default.CommandDocument, ApiDocumentJsonContext.Default.CommandDocument, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CommandDocument> GetCommandAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return GetAsync("commands/" + Uri.EscapeDataString(id), ApiDocumentJsonContext.Default.CommandDocument, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CommandDocument> ReplaceCommandAsync(CommandDocument command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrEmpty(command.Id);

        return SendJsonAsync(
            HttpMethod.Put,
            "commands/" + Uri.EscapeDataString(command.Id),
            command,
            ApiDocumentJsonContext.Default.CommandDocument,
            ApiDocumentJsonContext.Default.CommandDocument,
            cancellationToken,
            command.Revision);
    }

    /// <inheritdoc />
    public Task<ValidationReport> ValidateCommandAsync(CommandDocument draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return PostAsync("commands/validate", draft, ApiDocumentJsonContext.Default.CommandDocument, ApiJsonContext.Default.ValidationReport, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RunStarted> TestCommandAsync(CommandDocument draft, IReadOnlyDictionary<string, JsonElement> variables, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(variables);

        return PostAsync(
            "commands/test",
            new CommandTestRequest(draft, variables),
            ApiDocumentJsonContext.Default.CommandTestRequest,
            ApiJsonContext.Default.RunStarted,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ScriptSummaryList> GetScriptsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("scripts", ApiJsonContext.Default.ScriptSummaryList, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteCommandAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Delete,
            "commands/" + Uri.EscapeDataString(id),
            null,
            HttpCompletionOption.ResponseContentRead,
            streaming: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<RunStarted> StartRunAsync(string commandId, IReadOnlyDictionary<string, JsonElement> variables, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        ArgumentNullException.ThrowIfNull(variables);

        return PostAsync(
            "commands/" + Uri.EscapeDataString(commandId) + "/run",
            new RunStartRequest(variables),
            ApiJsonContext.Default.RunStartRequest,
            ApiJsonContext.Default.RunStarted,
            cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RunStreamItem> StreamRunAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        await foreach (ServerSentEvent item in ReadEventsAsync("runs/" + Uri.EscapeDataString(runId) + "/stream", cancellationToken))
        {
            if (item.Name == "output")
            {
                RunOutputEvent? output = ParseEvent(item.Data, ApiJsonContext.Default.RunOutputEvent);
                if (output is not null)
                {
                    yield return new RunStreamItem(Output: output);
                }
            }
            else if (item.Name == "end")
            {
                RunEndEvent? end = ParseEvent(item.Data, ApiJsonContext.Default.RunEndEvent);
                if (end is not null)
                {
                    yield return new RunStreamItem(End: end);
                    yield break;
                }
            }
        }
    }

    /// <inheritdoc />
    public Task<RunList> GetRunsAsync(int limit, CancellationToken cancellationToken = default)
    {
        return GetAsync("runs?limit=" + limit.ToString(CultureInfo.InvariantCulture), ApiJsonContext.Default.RunList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EventList> GetEventsAsync(int limit, CancellationToken cancellationToken = default)
    {
        return GetAsync("events?limit=" + limit.ToString(CultureInfo.InvariantCulture), ApiJsonContext.Default.EventList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MetricOverridesDocument> PutMetricOverrideAsync(string id, MetricOverride change, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(change);

        return SendJsonAsync(
            HttpMethod.Put,
            "metrics/" + Uri.EscapeDataString(id),
            change,
            ApiDocumentJsonContext.Default.MetricOverride,
            ApiDocumentJsonContext.Default.MetricOverridesDocument,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SensorList> GetSensorsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("sensors", ApiDocumentJsonContext.Default.SensorList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SensorDocument> CreateSensorAsync(SensorDocument sensor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        return PostAsync("sensors", sensor, ApiDocumentJsonContext.Default.SensorDocument, ApiDocumentJsonContext.Default.SensorDocument, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SensorDocument> ReplaceSensorAsync(SensorDocument sensor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentException.ThrowIfNullOrEmpty(sensor.Id);

        return SendJsonAsync(
            HttpMethod.Put,
            "sensors/" + Uri.EscapeDataString(sensor.Id),
            sensor,
            ApiDocumentJsonContext.Default.SensorDocument,
            ApiDocumentJsonContext.Default.SensorDocument,
            cancellationToken,
            sensor.Revision);
    }

    /// <inheritdoc />
    public async Task DeleteSensorAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Delete,
            "sensors/" + Uri.EscapeDataString(id),
            null,
            HttpCompletionOption.ResponseContentRead,
            streaming: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SensorTestResult> TestSensorAsync(SensorDocument draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return PostAsync("sensors/test", draft, ApiDocumentJsonContext.Default.SensorDocument, ApiJsonContext.Default.SensorTestResult, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SensorValue> GetSensorValueAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return GetAsync("sensors/" + Uri.EscapeDataString(id) + "/value", ApiJsonContext.Default.SensorValue, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SensorValue> SetSensorValueAsync(string id, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(value);

        return SendJsonAsync(
            HttpMethod.Put,
            "sensors/" + Uri.EscapeDataString(id) + "/value",
            new SensorValueRequest(TextElement(value)),
            ApiJsonContext.Default.SensorValueRequest,
            ApiJsonContext.Default.SensorValue,
            cancellationToken);
    }

    /// <summary>A JSON string with the text, so that the agent reads it according to the value type of the sensor, like the command line does.</summary>
    private static JsonElement TextElement(string text)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStringValue(text);
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <inheritdoc />
    public Task<ModeGroupList> GetModeGroupsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("modes", ApiDocumentJsonContext.Default.ModeGroupList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ModeGroupDocument> CreateModeGroupAsync(ModeGroupDocument group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        return PostAsync("modes", group, ApiDocumentJsonContext.Default.ModeGroupDocument, ApiDocumentJsonContext.Default.ModeGroupDocument, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ModeGroupDocument> ReplaceModeGroupAsync(ModeGroupDocument group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentException.ThrowIfNullOrEmpty(group.Id);

        return SendJsonAsync(
            HttpMethod.Put,
            "modes/" + Uri.EscapeDataString(group.Id),
            group,
            ApiDocumentJsonContext.Default.ModeGroupDocument,
            ApiDocumentJsonContext.Default.ModeGroupDocument,
            cancellationToken,
            group.Revision);
    }

    /// <inheritdoc />
    public async Task DeleteModeGroupAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Delete,
            "modes/" + Uri.EscapeDataString(id),
            null,
            HttpCompletionOption.ResponseContentRead,
            streaming: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ModeState> GetModeStateAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return GetAsync("modes/" + Uri.EscapeDataString(id) + "/switch", ApiJsonContext.Default.ModeState, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ModeSwitchStarted> SwitchModeAsync(string id, string mode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(mode);

        return PostAsync(
            "modes/" + Uri.EscapeDataString(id) + "/switch",
            new ModeSwitchRequest(mode),
            ApiJsonContext.Default.ModeSwitchRequest,
            ApiJsonContext.Default.ModeSwitchStarted,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TemplateList> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("templates", ApiDocumentJsonContext.Default.TemplateList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TemplateDetail> GetTemplateAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return GetAsync("templates/" + Uri.EscapeDataString(id), ApiDocumentJsonContext.Default.TemplateDetail, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TemplateInstantiation> InstantiateTemplateAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            "templates/" + Uri.EscapeDataString(id) + "/instantiate",
            null,
            HttpCompletionOption.ResponseContentRead,
            streaming: false,
            cancellationToken);
        return await ReadAsync(response, ApiDocumentJsonContext.Default.TemplateInstantiation, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SecretList> GetSecretsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("secrets", ApiJsonContext.Default.SecretList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SecretSummary> PutSecretAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        return SendJsonAsync(
            HttpMethod.Put,
            "secrets/" + Uri.EscapeDataString(name),
            new SecretWriteRequest(value),
            ApiJsonContext.Default.SecretWriteRequest,
            ApiJsonContext.Default.SecretSummary,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Delete,
            "secrets/" + Uri.EscapeDataString(name),
            null,
            HttpCompletionOption.ResponseContentRead,
            streaming: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PairingInfo> StartPairingAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "link/pairing", null, HttpCompletionOption.ResponseContentRead, streaming: false, cancellationToken);
        return await ReadAsync(response, ApiJsonContext.Default.PairingInfo, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PairingInfo?> GetPairingAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "link/pairing", null, HttpCompletionOption.ResponseContentRead, streaming: false, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        return await ReadAsync(response, ApiJsonContext.Default.PairingInfo, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CancelPairingAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, "link/pairing", null, HttpCompletionOption.ResponseContentRead, streaming: false, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RevokeLinkClientAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Delete,
            "link/clients/" + Uri.EscapeDataString(id),
            null,
            HttpCompletionOption.ResponseContentRead,
            streaming: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentSettingsDocument> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("settings", ApiDocumentJsonContext.Default.AgentSettingsDocument, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentSettingsDocument> PutSettingsAsync(AgentSettingsDocument settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return SendJsonAsync(
            HttpMethod.Put,
            "settings",
            settings,
            ApiDocumentJsonContext.Default.AgentSettingsDocument,
            ApiDocumentJsonContext.Default.AgentSettingsDocument,
            cancellationToken,
            settings.Revision);
    }

    /// <inheritdoc />
    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            "auth/password",
            JsonContent.Create(request, ApiJsonContext.Default.ChangePasswordRequest),
            HttpCompletionOption.ResponseContentRead,
            streaming: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task RestartAgentAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "agent/restart", null, HttpCompletionOption.ResponseContentRead, streaming: false, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RunList> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var path = new StringBuilder("runs?limit=").Append(query.Limit.ToString(CultureInfo.InvariantCulture));
        if (query.Offset > 0)
        {
            path.Append("&offset=").Append(query.Offset.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(query.Command))
        {
            path.Append("&command=").Append(Uri.EscapeDataString(query.Command));
        }

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            path.Append("&source=").Append(Uri.EscapeDataString(query.Source));
        }

        if (query.FailedOnly)
        {
            path.Append("&failed=true");
        }

        return GetAsync(path.ToString(), ApiJsonContext.Default.RunList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RunDetail> GetRunAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return GetAsync("runs/" + Uri.EscapeDataString(id), ApiJsonContext.Default.RunDetail, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EventList> QueryEventsAsync(int limit, string? type, CancellationToken cancellationToken = default)
    {
        string path = "events?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(type))
        {
            path += "&type=" + Uri.EscapeDataString(type);
        }

        return GetAsync(path, ApiJsonContext.Default.EventList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LogList> GetLogsAsync(string level, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(level);

        string path = "logs?level=" + Uri.EscapeDataString(level) + "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
        return GetAsync(path, ApiJsonContext.Default.LogList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<DownloadedFile> ExportConfigAsync(CancellationToken cancellationToken = default)
    {
        return DownloadAsync("config/export", "qoffeeautomat-config.zip", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConfigImportPreview> PreviewImportAsync(byte[] archive, bool includeSettings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        try
        {
            using HttpResponseMessage response = await SendAsync(
                HttpMethod.Post,
                ImportPath("config/import/preview", includeSettings),
                ZipContent(archive),
                HttpCompletionOption.ResponseContentRead,
                streaming: false,
                cancellationToken);
            return await ReadAsync(response, ApiJsonContext.Default.ConfigImportPreview, cancellationToken);
        }
        catch (ApiException exception) when (exception.StatusCode == 422)
        {
            // An archive that does not validate is the answer the preview exists for.
            return new ConfigImportPreview(false, [], exception.ValidationErrors);
        }
    }

    /// <inheritdoc />
    public async Task<ConfigImportResult> ImportConfigAsync(byte[] archive, bool includeSettings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            ImportPath("config/import", includeSettings),
            ZipContent(archive),
            HttpCompletionOption.ResponseContentRead,
            streaming: false,
            cancellationToken);
        return await ReadAsync(response, ApiJsonContext.Default.ConfigImportResult, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RevisionList> GetRevisionsAsync(string kind, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);

        return GetAsync("config/revisions/" + Uri.EscapeDataString(kind) + "/" + Uri.EscapeDataString(id), ApiJsonContext.Default.RevisionList, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RestoreRevisionAsync(string kind, string id, int revision, int? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);

        string path = "config/revisions/" + Uri.EscapeDataString(kind) + "/" + Uri.EscapeDataString(id)
            + "/" + revision.ToString(CultureInfo.InvariantCulture) + "/restore";
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            path,
            null,
            HttpCompletionOption.ResponseContentRead,
            streaming: false,
            cancellationToken,
            expectedRevision);
    }

    /// <inheritdoc />
    public Task<DownloadedFile> DownloadDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        return DownloadAsync("diagnostics", "qoffeeautomat-diagnostics.zip", cancellationToken);
    }

    private static string ImportPath(string path, bool includeSettings)
    {
        return includeSettings ? path + "?settings=true" : path;
    }

    private static ByteArrayContent ZipContent(byte[] archive)
    {
        var content = new ByteArrayContent(archive);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return content;
    }

    private async Task<DownloadedFile> DownloadAsync(string path, string fallbackName, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null, HttpCompletionOption.ResponseContentRead, streaming: false, cancellationToken);
        try
        {
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            ContentDispositionHeaderValue? disposition = response.Content.Headers.ContentDisposition;
            string name = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"') ?? string.Empty;
            string type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return new DownloadedFile(name.Length > 0 ? name : fallbackName, type, bytes);
        }
        catch (HttpRequestException exception)
        {
            throw NetworkError(exception);
        }
    }

    private async IAsyncEnumerable<ServerSentEvent> ReadEventsAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The request lives as long as the enumeration: disposing the enumerator disposes the response, which closes
        // the connection, and the agent stops sampling when its last subscriber is gone.
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null, HttpCompletionOption.ResponseHeadersRead, streaming: true, cancellationToken);
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body, Encoding.UTF8);
        IAsyncEnumerator<ServerSentEvent> events = ServerSentEventReader.ReadAsync(reader, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await events.MoveNextAsync();
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    throw NetworkError(exception);
                }

                if (!moved)
                {
                    yield break;
                }

                yield return events.Current;
            }
        }
        finally
        {
            await events.DisposeAsync();
        }
    }

    private static T? ParseEvent<T>(string data, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(data, typeInfo);
        }
        catch (JsonException)
        {
            // One malformed event must not end the stream.
            return null;
        }
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null, HttpCompletionOption.ResponseContentRead, streaming: false, cancellationToken);
        return await ReadAsync(response, typeInfo, cancellationToken);
    }

    private Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResponse> responseInfo,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        return SendJsonAsync(HttpMethod.Post, path, body, requestInfo, responseInfo, cancellationToken);
    }

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResponse> responseInfo,
        CancellationToken cancellationToken,
        int? ifMatchRevision = null)
        where TResponse : class
    {
        using HttpResponseMessage response = await SendAsync(
            method,
            path,
            JsonContent.Create(body, requestInfo),
            HttpCompletionOption.ResponseContentRead,
            streaming: false,
            cancellationToken,
            ifMatchRevision);
        return await ReadAsync(response, responseInfo, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        HttpCompletionOption completion,
        bool streaming,
        CancellationToken cancellationToken,
        int? ifMatchRevision = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(ApiBase + path, UriKind.Relative)) { Content = content };

        if (ifMatchRevision is { } revision)
        {
            // The ETag of a document is its revision in quotes (PLAN.md S08).
            request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"" + revision.ToString(CultureInfo.InvariantCulture) + "\""));
        }

        // The session cookie is HttpOnly and SameSite=Strict: the browser attaches it to requests of this origin.
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        if (streaming)
        {
            // Blazor WebAssembly on .NET 10 hands the body over as it arrives (response streaming is on by default),
            // which is what an event stream needs; HttpCompletionOption.ResponseHeadersRead is set by the caller.
            request.Headers.Accept.ParseAdd(EventStreamMediaType);
        }

        if (ChangesState(method))
        {
            request.Headers.Add(ApiHeaders.CsrfName, ApiHeaders.CsrfValue);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw NetworkError(exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The HTTP client gave up (its own time limit), which is not the caller cancelling.
            throw NetworkError(exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await ToExceptionAsync(response, cancellationToken);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken)
                ?? throw InvalidAnswer(response, null);
        }
        catch (JsonException exception)
        {
            throw InvalidAnswer(response, exception);
        }
        catch (HttpRequestException exception)
        {
            throw NetworkError(exception);
        }
    }

    private static async Task<ApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;
        ApiProblem? problem = null;
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                problem = JsonSerializer.Deserialize(body, ApiJsonContext.Default.ApiProblem);
            }
        }
        catch (JsonException)
        {
            // Not problem details: the status alone describes the error.
        }
        catch (HttpRequestException)
        {
            // The body could not be read: same.
        }

        problem ??= new ApiProblem("about:blank", response.ReasonPhrase ?? "Error", status);
        int? retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;
        return new ApiException(status, problem, retryAfter);
    }

    private static ApiException InvalidAnswer(HttpResponseMessage response, Exception? cause)
    {
        int status = (int)response.StatusCode;
        var problem = new ApiProblem("about:blank", "Invalid answer", status, "The answer of the agent is not understood.", "invalid_response");
        return new ApiException(status, problem, innerException: cause);
    }

    private static ApiException NetworkError(Exception cause)
    {
        var problem = new ApiProblem("about:blank", "Network error", 0, "The agent cannot be reached.", ApiException.NetworkErrorCode);
        return new ApiException(0, problem, innerException: cause);
    }

    private static bool ChangesState(HttpMethod method)
    {
        return method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options && method != HttpMethod.Trace;
    }
}
