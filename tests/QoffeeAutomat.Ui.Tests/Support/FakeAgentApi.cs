using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Ui.Tests.Support;

/// <summary>
/// An agent for component tests: answers with data that a test sets, records the calls, and keeps count of the live
/// metric streams that are open, so that a test can assert that leaving a page closes its stream.
/// </summary>
internal sealed class FakeAgentApi : IAgentApi
{
    private readonly object _gate = new();
    private readonly List<Channel<MetricStreamUpdate>> _streams = [];
    private readonly Channel<RunStreamItem> _runStream = Channel.CreateUnbounded<RunStreamItem>();

    public SetupState? Setup { get; set; }

    public AuthMeResponse? Me { get; set; }

    public ApiException? SetupFailure { get; set; }

    public ApiException? LoginFailure { get; set; }

    public ApiException? StatusFailure { get; set; }

    public AgentStatus Status { get; set; } = Samples.Status();

    public LinkClientList Clients { get; set; } = Samples.Clients();

    public MetricList Catalog { get; set; } = Samples.Catalog();

    public MetricHistoryResponse History { get; set; } = Samples.History();

    public RunList Runs { get; set; } = Samples.Runs();

    public EventList Events { get; set; } = Samples.Events();

    /// <summary>The commands that the agent has; the fake keeps them up to date when a test patches, creates or deletes.</summary>
    public List<CommandDocument> Commands { get; set; } = Samples.Commands();

    public ApiException? CommandsFailure { get; set; }

    public ApiException? PatchFailure { get; set; }

    public ApiException? CreateFailure { get; set; }

    public ApiException? DeleteFailure { get; set; }

    public ApiException? StartRunFailure { get; set; }

    /// <summary>When set, a patch waits until the test completes it; the switch is then seen while the save is under way.</summary>
    public TaskCompletionSource? PatchGate { get; set; }

    public int CommandsCalls { get; private set; }

    public ApiException? GetCommandFailure { get; set; }

    public ApiException? ReplaceFailure { get; set; }

    public ApiException? TestFailure { get; set; }

    public ApiException? ValidateFailure { get; set; }

    /// <summary>The script files that the agent has.</summary>
    public ScriptSummaryList Scripts { get; set; } = new([]);

    /// <summary>What the agent says about a draft; by default every draft is valid.</summary>
    public Func<CommandDocument, ValidationReport> ValidationHandler { get; set; } = _ => new ValidationReport(true, []);

    /// <summary>What the agent says when a draft is tested, as a failure to throw; <c>null</c> accepts the run.</summary>
    public Func<CommandDocument, IReadOnlyDictionary<string, JsonElement>, ApiException?>? TestHandler { get; set; }

    public List<CommandDocument> Validated { get; } = [];

    public List<CommandDocument> Replaced { get; } = [];

    public List<(CommandDocument Draft, IReadOnlyDictionary<string, JsonElement> Variables)> TestRequests { get; } = [];

    public List<(string Id, CommandPatch Patch)> Patches { get; } = [];

    public List<CommandDocument> Created { get; } = [];

    public List<string> Deleted { get; } = [];

    public List<(string CommandId, IReadOnlyDictionary<string, JsonElement> Variables)> RunRequests { get; } = [];

    public int RunStreamsOpened { get; private set; }

    public int RunStreamsActive { get; private set; }

    public string? LastStreamedRun { get; private set; }

    public List<SetupRequest> SetupRequests { get; } = [];

    public List<LoginRequest> LoginRequests { get; } = [];

    public int LogOutCalls { get; private set; }

    public int StatusCalls { get; private set; }

    public int MetricCalls { get; private set; }

    /// <summary>The number of times that a stream was asked for, including the ones that failed to open.</summary>
    public int StreamAttempts { get; private set; }

    /// <summary>The number of streams that are open now.</summary>
    public int StreamsActive { get; private set; }

    /// <summary>The number of streams that were closed (disposed or cancelled by the caller).</summary>
    public int StreamsClosed { get; private set; }

    /// <summary>How many of the next stream requests fail with a network error before the stream is open.</summary>
    public int FailNextStreams { get; set; }

    public IReadOnlyList<string> LastStreamIds { get; private set; } = [];

    /// <summary>Sends an update to every open stream.</summary>
    public void Push(MetricStreamUpdate update)
    {
        lock (_gate)
        {
            foreach (Channel<MetricStreamUpdate> stream in _streams)
            {
                stream.Writer.TryWrite(update);
            }
        }
    }

    /// <summary>Ends every open stream the way a restarting agent does.</summary>
    public void EndStreams()
    {
        lock (_gate)
        {
            foreach (Channel<MetricStreamUpdate> stream in _streams)
            {
                stream.Writer.TryComplete();
            }
        }
    }

    /// <summary>The custom sensors that the agent has; the fake keeps them up to date when a test creates, replaces or deletes.</summary>
    public List<SensorDocument> Sensors { get; set; } = [];

    public ApiException? SensorsFailure { get; set; }

    public ApiException? SensorCreateFailure { get; set; }

    public ApiException? SensorReplaceFailure { get; set; }

    public ApiException? SensorDeleteFailure { get; set; }

    public ApiException? SensorTestFailure { get; set; }

    /// <summary>What the agent says when a draft sensor is sampled; by default the sensor yields 42.</summary>
    public Func<SensorDocument, SensorTestResult> SensorTestHandler { get; set; } = _ => new SensorTestResult(true, Samples.Number(42), null, 12);

    public List<SensorDocument> CreatedSensors { get; } = [];

    public List<SensorDocument> ReplacedSensors { get; } = [];

    public List<string> DeletedSensors { get; } = [];

    public List<SensorDocument> TestedSensors { get; } = [];

    public ApiException? OverrideFailure { get; set; }

    /// <summary>When set, saving an override waits until the test completes it.</summary>
    public TaskCompletionSource? OverrideGate { get; set; }

    public List<(string Id, MetricOverride Override)> Overrides { get; } = [];

    public int SettingsCalls { get; private set; }

    public AgentSettingsDocument Settings { get; set; } = new(DocumentSchema.CurrentVersion, "Study PC", Revision: 3);

    public ApiException? SettingsFailure { get; set; }

    public ApiException? SettingsPutFailure { get; set; }

    public List<AgentSettingsDocument> SettingsPuts { get; } = [];

    public ApiException? PairingStartFailure { get; set; }

    public ApiException? PairingReadFailure { get; set; }

    public ApiException? RevokeFailure { get; set; }

    /// <summary>How long a code that the fake hands out is valid.</summary>
    public TimeSpan PairingLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public string PairingCode { get; set; } = "123456";

    public string Fingerprint { get; set; } = "AB:CD:EF:01:23:45:67:89";

    /// <summary>The active pairing code; <c>null</c> when there is none.</summary>
    public PairingInfo? Pairing { get; set; }

    public int PairingStarts { get; private set; }

    public int PairingCancels { get; private set; }

    public int PairingReads { get; private set; }

    public int ClientCalls { get; private set; }

    public List<string> Revoked { get; } = [];

    /// <summary>A client pairs with the active code: the code is used up and the client is in the list.</summary>
    public void CompletePairing(string name = "Home Assistant (new)")
    {
        Pairing = null;
        Clients = new LinkClientList([.. Clients.Clients, new LinkClientInfo("new-client", name, "homeassistant", DateTimeOffset.UtcNow, null, 0)]);
    }

    public Task<SetupState?> GetSetupStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Setup);

    public Task<AuthMeResponse> SetUpAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        SetupRequests.Add(request);
        if (SetupFailure is not null)
        {
            return Task.FromException<AuthMeResponse>(SetupFailure);
        }

        var me = new AuthMeResponse(true, "administrator");
        Me = me;
        Setup = null;
        return Task.FromResult(me);
    }

    public Task<AuthMeResponse> LogInAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        LoginRequests.Add(request);
        if (LoginFailure is not null)
        {
            return Task.FromException<AuthMeResponse>(LoginFailure);
        }

        var me = new AuthMeResponse(true, "administrator");
        Me = me;
        return Task.FromResult(me);
    }

    public Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        LogOutCalls++;
        Me = null;
        return Task.CompletedTask;
    }

    public Task<AuthMeResponse?> GetMeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Me);

    public Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        StatusCalls++;
        return StatusFailure is null ? Task.FromResult(Status) : Task.FromException<AgentStatus>(StatusFailure);
    }

    public Task<LinkClientList> GetLinkClientsAsync(CancellationToken cancellationToken = default)
    {
        ClientCalls++;
        return Task.FromResult(Clients);
    }

    public async Task<MetricOverridesDocument> PutMetricOverrideAsync(string id, MetricOverride change, CancellationToken cancellationToken = default)
    {
        Overrides.Add((id, change));
        if (OverrideGate is not null)
        {
            await OverrideGate.Task.WaitAsync(cancellationToken);
        }

        if (OverrideFailure is not null)
        {
            throw OverrideFailure;
        }

        List<MetricInfo> metrics = [.. Catalog.Metrics];
        int index = metrics.FindIndex(metric => metric.Id == id);
        if (index < 0)
        {
            throw new ApiException(404, Samples.Problem(404, "unknown_metric", "There is no metric with this id."));
        }

        MetricInfo current = metrics[index];
        metrics[index] = current with
        {
            Enabled = change.Enabled ?? current.Enabled,
            ExposeToHa = change.ExposeToHa ?? current.ExposeToHa,
            IntervalSeconds = change.IntervalSeconds ?? current.IntervalSeconds,
            Deadband = change.Deadband ?? current.Deadband,
        };
        Catalog = new MetricList(metrics);
        return new MetricOverridesDocument(DocumentSchema.CurrentVersion, new Dictionary<string, MetricOverride> { [id] = change }, Revision: Overrides.Count);
    }

    public Task<SensorList> GetSensorsAsync(CancellationToken cancellationToken = default)
    {
        return SensorsFailure is null
            ? Task.FromResult(new SensorList([.. Sensors]))
            : Task.FromException<SensorList>(SensorsFailure);
    }

    public Task<SensorDocument> CreateSensorAsync(SensorDocument sensor, CancellationToken cancellationToken = default)
    {
        CreatedSensors.Add(sensor);
        if (SensorCreateFailure is not null)
        {
            return Task.FromException<SensorDocument>(SensorCreateFailure);
        }

        if (Sensors.Exists(existing => existing.Id == sensor.Id))
        {
            return Task.FromException<SensorDocument>(new ApiException(409, Samples.Problem(409, "already_exists", "A sensor with this id exists.")));
        }

        SensorDocument stored = sensor with { Revision = 1 };
        Sensors.Add(stored);
        Sensors.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return Task.FromResult(stored);
    }

    public Task<SensorDocument> ReplaceSensorAsync(SensorDocument sensor, CancellationToken cancellationToken = default)
    {
        ReplacedSensors.Add(sensor);
        if (SensorReplaceFailure is not null)
        {
            return Task.FromException<SensorDocument>(SensorReplaceFailure);
        }

        int index = Sensors.FindIndex(candidate => candidate.Id == sensor.Id);
        if (index < 0)
        {
            return Task.FromException<SensorDocument>(new ApiException(404, Samples.Problem(404, "unknown_sensor", "There is no sensor with this id.")));
        }

        if (sensor.Revision is { } expected && expected != Sensors[index].Revision)
        {
            return Task.FromException<SensorDocument>(new ApiException(412, Samples.Problem(412, "revision_mismatch", "The sensor was changed meanwhile.")));
        }

        SensorDocument stored = sensor with { Revision = (Sensors[index].Revision ?? 0) + 1 };
        Sensors[index] = stored;
        return Task.FromResult(stored);
    }

    public Task DeleteSensorAsync(string id, CancellationToken cancellationToken = default)
    {
        DeletedSensors.Add(id);
        if (SensorDeleteFailure is not null)
        {
            return Task.FromException(SensorDeleteFailure);
        }

        Sensors.RemoveAll(sensor => sensor.Id == id);
        return Task.CompletedTask;
    }

    public Task<SensorTestResult> TestSensorAsync(SensorDocument draft, CancellationToken cancellationToken = default)
    {
        TestedSensors.Add(draft);
        return SensorTestFailure is null
            ? Task.FromResult(SensorTestHandler(draft))
            : Task.FromException<SensorTestResult>(SensorTestFailure);
    }

    public Task<PairingInfo> StartPairingAsync(CancellationToken cancellationToken = default)
    {
        PairingStarts++;
        if (PairingStartFailure is not null)
        {
            return Task.FromException<PairingInfo>(PairingStartFailure);
        }

        PairingInfo info = new(PairingCode, DateTimeOffset.UtcNow + PairingLifetime, Fingerprint);
        Pairing = info;
        return Task.FromResult(info);
    }

    public Task<PairingInfo?> GetPairingAsync(CancellationToken cancellationToken = default)
    {
        PairingReads++;
        if (PairingReadFailure is not null)
        {
            return Task.FromException<PairingInfo?>(PairingReadFailure);
        }

        // The agent answers 204 for a code that expired.
        PairingInfo? active = Pairing is { } info && info.ExpiresAt > DateTimeOffset.UtcNow ? info : null;
        return Task.FromResult(active);
    }

    public Task CancelPairingAsync(CancellationToken cancellationToken = default)
    {
        PairingCancels++;
        Pairing = null;
        return Task.CompletedTask;
    }

    public Task RevokeLinkClientAsync(string id, CancellationToken cancellationToken = default)
    {
        Revoked.Add(id);
        if (RevokeFailure is not null)
        {
            return Task.FromException(RevokeFailure);
        }

        if (!Clients.Clients.Any(client => client.Id == id))
        {
            return Task.FromException(new ApiException(404, Samples.Problem(404, "unknown_client", "There is no paired client with this id.")));
        }

        Clients = new LinkClientList([.. Clients.Clients.Where(client => client.Id != id)]);
        return Task.CompletedTask;
    }

    public Task<AgentSettingsDocument> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        SettingsCalls++;
        return SettingsFailure is null ? Task.FromResult(Settings) : Task.FromException<AgentSettingsDocument>(SettingsFailure);
    }

    public Task<AgentSettingsDocument> PutSettingsAsync(AgentSettingsDocument settings, CancellationToken cancellationToken = default)
    {
        SettingsPuts.Add(settings);
        if (SettingsPutFailure is not null)
        {
            return Task.FromException<AgentSettingsDocument>(SettingsPutFailure);
        }

        if (settings.Revision is { } expected && expected != Settings.Revision)
        {
            return Task.FromException<AgentSettingsDocument>(new ApiException(412, Samples.Problem(412, "revision_mismatch", "The settings were changed meanwhile.")));
        }

        Settings = settings with { Revision = (Settings.Revision ?? 0) + 1 };
        return Task.FromResult(Settings);
    }

    public List<ChangePasswordRequest> PasswordChanges { get; } = [];

    public ApiException? PasswordFailure { get; set; }

    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        PasswordChanges.Add(request);
        return PasswordFailure is null ? Task.CompletedTask : Task.FromException(PasswordFailure);
    }

    public int Restarts { get; private set; }

    public ApiException? RestartFailure { get; set; }

    public Task RestartAgentAsync(CancellationToken cancellationToken = default)
    {
        Restarts++;
        return RestartFailure is null ? Task.CompletedTask : Task.FromException(RestartFailure);
    }

    /// <summary>The queries of <see cref="QueryRunsAsync"/>, in order.</summary>
    public List<RunQuery> RunQueries { get; } = [];

    public ApiException? RunsFailure { get; set; }

    /// <summary>The details that <see cref="GetRunAsync"/> answers with, by run id.</summary>
    public Dictionary<string, RunDetail> RunDetails { get; } = new(StringComparer.Ordinal) { ["r1"] = Samples.RunDetail("r1"), ["r2"] = Samples.RunDetail("r2") };

    public Task<RunList> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken = default)
    {
        RunQueries.Add(query);
        if (RunsFailure is not null)
        {
            return Task.FromException<RunList>(RunsFailure);
        }

        IEnumerable<RunSummary> runs = Runs.Runs;
        if (!string.IsNullOrEmpty(query.Command))
        {
            runs = runs.Where(run => run.Command == query.Command);
        }

        if (!string.IsNullOrEmpty(query.Source))
        {
            runs = runs.Where(run => run.Source == query.Source);
        }

        if (query.FailedOnly)
        {
            runs = runs.Where(run => !run.Succeeded);
        }

        RunSummary[] all = [.. runs];
        RunSummary[] page = [.. all.Skip(query.Offset).Take(query.Limit)];
        return Task.FromResult(new RunList(page, query.Limit, query.Offset, query.Offset + page.Length < all.Length));
    }

    public Task<RunDetail> GetRunAsync(string id, CancellationToken cancellationToken = default)
    {
        return RunDetails.TryGetValue(id, out RunDetail? detail)
            ? Task.FromResult(detail)
            : Task.FromException<RunDetail>(new ApiException(404, Samples.Problem(404, "not_found", "There is no run with this id.")));
    }

    public List<(int Limit, string? Type)> EventQueries { get; } = [];

    public Task<EventList> QueryEventsAsync(int limit, string? type, CancellationToken cancellationToken = default)
    {
        EventQueries.Add((limit, type));
        IEnumerable<EventInfo> events = Events.Events;
        if (!string.IsNullOrEmpty(type))
        {
            events = events.Where(item => item.Type == type);
        }

        return Task.FromResult(new EventList([.. events.Take(limit)]));
    }

    /// <summary>The log of the agent, oldest first; <see cref="GetLogsAsync"/> filters it by level.</summary>
    public List<LogEntry> LogEntries { get; set; } = Samples.Logs();

    public List<(string Level, int Limit)> LogQueries { get; } = [];

    public ApiException? LogsFailure { get; set; }

    public Task<LogList> GetLogsAsync(string level, int limit, CancellationToken cancellationToken = default)
    {
        LogQueries.Add((level, limit));
        if (LogsFailure is not null)
        {
            return Task.FromException<LogList>(LogsFailure);
        }

        int minimum = Samples.LogRank(level);
        LogEntry[] matching = [.. LogEntries.Where(entry => Samples.LogRank(entry.Level) >= minimum)];
        return Task.FromResult(new LogList([.. matching.Skip(Math.Max(0, matching.Length - limit))]));
    }

    public DownloadedFile ExportFile { get; set; } = new("qoffeeautomat-config-20260101-120000.zip", "application/zip", [1, 2, 3]);

    public DownloadedFile DiagnosticsFile { get; set; } = new("qoffeeautomat-diagnostics-20260101-120000.zip", "application/zip", [4, 5, 6]);

    public ApiException? ExportFailure { get; set; }

    public int Exports { get; private set; }

    public int DiagnosticsDownloads { get; private set; }

    public Task<DownloadedFile> ExportConfigAsync(CancellationToken cancellationToken = default)
    {
        Exports++;
        return ExportFailure is null ? Task.FromResult(ExportFile) : Task.FromException<DownloadedFile>(ExportFailure);
    }

    public Task<DownloadedFile> DownloadDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        DiagnosticsDownloads++;
        return ExportFailure is null ? Task.FromResult(DiagnosticsFile) : Task.FromException<DownloadedFile>(ExportFailure);
    }

    /// <summary>What <see cref="PreviewImportAsync"/> answers with.</summary>
    public ConfigImportPreview Preview { get; set; } = Samples.ImportPreview();

    public ApiException? PreviewFailure { get; set; }

    public List<(byte[] Archive, bool IncludeSettings)> Previews { get; } = [];

    public ConfigImportResult ImportResult { get; set; } = new(1, 1, 0, false, false);

    public ApiException? ImportFailure { get; set; }

    public List<(byte[] Archive, bool IncludeSettings)> Imports { get; } = [];

    public Task<ConfigImportPreview> PreviewImportAsync(byte[] archive, bool includeSettings, CancellationToken cancellationToken = default)
    {
        Previews.Add((archive, includeSettings));
        return PreviewFailure is null ? Task.FromResult(Preview) : Task.FromException<ConfigImportPreview>(PreviewFailure);
    }

    public Task<ConfigImportResult> ImportConfigAsync(byte[] archive, bool includeSettings, CancellationToken cancellationToken = default)
    {
        Imports.Add((archive, includeSettings));
        return ImportFailure is null ? Task.FromResult(ImportResult) : Task.FromException<ConfigImportResult>(ImportFailure);
    }

    /// <summary>The revisions that <see cref="GetRevisionsAsync"/> answers with, by kind and id.</summary>
    public Dictionary<(string Kind, string Id), RevisionList> Revisions { get; } = new()
    {
        [(RevisionKinds.Command, "lock_screen")] = Samples.RevisionsOf(RevisionKinds.Command, "lock_screen", 3),
        [(RevisionKinds.Settings, "settings")] = Samples.RevisionsOf(RevisionKinds.Settings, "settings", 3),
    };

    public ApiException? RevisionsFailure { get; set; }

    public ApiException? RestoreFailure { get; set; }

    public List<(string Kind, string Id, int Revision, int? Expected)> Restores { get; } = [];

    public Task<RevisionList> GetRevisionsAsync(string kind, string id, CancellationToken cancellationToken = default)
    {
        if (RevisionsFailure is not null)
        {
            return Task.FromException<RevisionList>(RevisionsFailure);
        }

        return Revisions.TryGetValue((kind, id), out RevisionList? list)
            ? Task.FromResult(list)
            : Task.FromException<RevisionList>(new ApiException(404, Samples.Problem(404, "not_found", "There is no such document.")));
    }

    public Task RestoreRevisionAsync(string kind, string id, int revision, int? expectedRevision, CancellationToken cancellationToken = default)
    {
        Restores.Add((kind, id, revision, expectedRevision));
        if (RestoreFailure is not null)
        {
            return Task.FromException(RestoreFailure);
        }

        if (Revisions.TryGetValue((kind, id), out RevisionList? list) && list.Current is { } current)
        {
            // The restored revision is stored as a new one.
            int next = current + 1;
            Revisions[(kind, id)] = list with { Current = next, Revisions = [new RevisionEntry(next, Samples.Now), .. list.Revisions] };
        }

        return Task.CompletedTask;
    }

    public Task<MetricList> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        MetricCalls++;
        return Task.FromResult(Catalog);
    }

    public Task<MetricHistoryResponse> GetMetricHistoryAsync(IReadOnlyList<string> metricIds, int seconds, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(History);
    }

    public Task<RunList> GetRunsAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult(Runs);

    public Task<EventList> GetEventsAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult(Events);

    public Task<CommandList> GetCommandsAsync(CancellationToken cancellationToken = default)
    {
        CommandsCalls++;
        return CommandsFailure is null
            ? Task.FromResult(new CommandList([.. Commands]))
            : Task.FromException<CommandList>(CommandsFailure);
    }

    public async Task<CommandDocument> PatchCommandAsync(string id, CommandPatch patch, CancellationToken cancellationToken = default)
    {
        Patches.Add((id, patch));
        if (PatchGate is not null)
        {
            await PatchGate.Task.WaitAsync(cancellationToken);
        }

        if (PatchFailure is not null)
        {
            throw PatchFailure;
        }

        int index = Commands.FindIndex(command => command.Id == id);
        if (index < 0)
        {
            throw new ApiException(404, Samples.Problem(404, "unknown_command", "There is no command with this id."));
        }

        CommandDocument changed = Commands[index] with
        {
            Enabled = patch.Enabled ?? Commands[index].Enabled,
            ExposeToHa = patch.ExposeToHa ?? Commands[index].ExposeToHa,
            Revision = (Commands[index].Revision ?? 0) + 1,
        };
        Commands[index] = changed;
        return changed;
    }

    public Task<CommandDocument> CreateCommandAsync(CommandDocument command, CancellationToken cancellationToken = default)
    {
        Created.Add(command);
        if (CreateFailure is not null)
        {
            return Task.FromException<CommandDocument>(CreateFailure);
        }

        if (Commands.Exists(existing => existing.Id == command.Id))
        {
            return Task.FromException<CommandDocument>(new ApiException(409, Samples.Problem(409, "already_exists", "A command with this id exists.")));
        }

        CommandDocument stored = command with { Revision = 1 };
        Commands.Add(stored);
        Commands.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return Task.FromResult(stored);
    }

    public Task<CommandDocument> GetCommandAsync(string id, CancellationToken cancellationToken = default)
    {
        if (GetCommandFailure is not null)
        {
            return Task.FromException<CommandDocument>(GetCommandFailure);
        }

        CommandDocument? command = Commands.Find(candidate => candidate.Id == id);
        return command is null
            ? Task.FromException<CommandDocument>(new ApiException(404, Samples.Problem(404, "unknown_command", "There is no command with this id.")))
            : Task.FromResult(command);
    }

    public Task<CommandDocument> ReplaceCommandAsync(CommandDocument command, CancellationToken cancellationToken = default)
    {
        Replaced.Add(command);
        if (ReplaceFailure is not null)
        {
            return Task.FromException<CommandDocument>(ReplaceFailure);
        }

        int index = Commands.FindIndex(candidate => candidate.Id == command.Id);
        if (index < 0)
        {
            return Task.FromException<CommandDocument>(new ApiException(404, Samples.Problem(404, "unknown_command", "There is no command with this id.")));
        }

        if (command.Revision is { } expected && expected != Commands[index].Revision)
        {
            return Task.FromException<CommandDocument>(new ApiException(412, Samples.Problem(412, "revision_mismatch", "The command was changed meanwhile.")));
        }

        CommandDocument stored = command with { Revision = (Commands[index].Revision ?? 0) + 1 };
        Commands[index] = stored;
        return Task.FromResult(stored);
    }

    public Task<ValidationReport> ValidateCommandAsync(CommandDocument draft, CancellationToken cancellationToken = default)
    {
        Validated.Add(draft);
        return ValidateFailure is null
            ? Task.FromResult(ValidationHandler(draft))
            : Task.FromException<ValidationReport>(ValidateFailure);
    }

    public Task<RunStarted> TestCommandAsync(CommandDocument draft, IReadOnlyDictionary<string, JsonElement> variables, CancellationToken cancellationToken = default)
    {
        TestRequests.Add((draft, variables));
        ApiException? failure = TestFailure ?? TestHandler?.Invoke(draft, variables);
        if (failure is not null)
        {
            return Task.FromException<RunStarted>(failure);
        }

        return Task.FromResult(new RunStarted("test-" + TestRequests.Count.ToString(CultureInfo.InvariantCulture), draft.Id, "test"));
    }

    public Task<ScriptSummaryList> GetScriptsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Scripts);

    public Task DeleteCommandAsync(string id, CancellationToken cancellationToken = default)
    {
        Deleted.Add(id);
        if (DeleteFailure is not null)
        {
            return Task.FromException(DeleteFailure);
        }

        Commands.RemoveAll(command => command.Id == id);
        return Task.CompletedTask;
    }

    public Task<RunStarted> StartRunAsync(string commandId, IReadOnlyDictionary<string, JsonElement> variables, CancellationToken cancellationToken = default)
    {
        RunRequests.Add((commandId, variables));
        if (StartRunFailure is not null)
        {
            return Task.FromException<RunStarted>(StartRunFailure);
        }

        return Task.FromResult(new RunStarted("run-" + RunRequests.Count.ToString(CultureInfo.InvariantCulture), commandId, "ui"));
    }

    /// <summary>Sends an item to the output stream of the run; the end item ends the stream.</summary>
    public void PushRun(RunStreamItem item) => _runStream.Writer.TryWrite(item);

    /// <summary>Ends the output stream of the run without an end item, the way a lost connection does.</summary>
    public void EndRunStream() => _runStream.Writer.TryComplete();

    public async IAsyncEnumerable<RunStreamItem> StreamRunAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RunStreamsOpened++;
        RunStreamsActive++;
        LastStreamedRun = runId;
        try
        {
            await foreach (RunStreamItem item in _runStream.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
                if (item.End is not null)
                {
                    yield break;
                }
            }
        }
        finally
        {
            RunStreamsActive--;
        }
    }

    public async IAsyncEnumerable<MetricStreamUpdate> StreamMetricsAsync(
        IReadOnlyList<string> metricIds,
        int intervalSeconds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<MetricStreamUpdate> channel = Channel.CreateUnbounded<MetricStreamUpdate>();
        lock (_gate)
        {
            StreamAttempts++;
            if (FailNextStreams > 0)
            {
                FailNextStreams--;
                throw new ApiException(0, Samples.Problem(0, ApiException.NetworkErrorCode, "The agent cannot be reached."));
            }

            _streams.Add(channel);
            StreamsActive++;
            LastStreamIds = [.. metricIds];
        }

        try
        {
            await foreach (MetricStreamUpdate update in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            lock (_gate)
            {
                _streams.Remove(channel);
                StreamsActive--;
                StreamsClosed++;
            }
        }
    }
}
