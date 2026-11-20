using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Config.Rollout;

/// <summary>
/// The configuration roll-out of the hub (PLAN.md S20, D-027, T909): it connects the configuration service (T907) to the agent gateway (T906). Whenever an agent
/// may lack the desired generation the hub computed for it, it sends <c>config_desired</c> over the agent link, serves the documents the agent asks for in
/// <c>config_documents</c> frames below the frame limit, and records what the agent reports in <c>config_status</c>: the generation it applied and the state and errors
/// of every document. The triggers are
/// <list type="bullet">
/// <item><description>a publish, a roll-back, an assignment or the removal of a profile (<see cref="ConfigService.OnGenerationsChanged"/>),</description></item>
/// <item><description>a change of the tags of an agent (<see cref="OnAgentTagsChangedAsync"/>),</description></item>
/// <item><description>an agent that connects: the desired state is recomputed (a new agent, tags that changed while it was offline) and pushed when the agent lacks it,</description></item>
/// <item><description>a <c>config_status</c> of the agent, which is sent after every handshake and after every attempt to apply a generation.</description></item>
/// </list>
/// An agent is only ever sent the current generation: while it is offline, generations pass and it receives the newest one on reconnect. Documents it already
/// carries are not sent: the agent answers <c>config_desired</c> with the list it lacks. The work of one agent is done one step after the other, and what arrives
/// meanwhile is merged (only the newest status counts), so a burst of changes costs one push. A generation is sent at most once per connection, so an agent that
/// refuses it does not make the hub send it again and again; the next connection, a newer generation or <see cref="RequestSync"/> try again.
/// </summary>
public sealed class ConfigRolloutService : IAgentGatewayObserver, IHostedService, IDisposable
{
    /// <summary>The most document results of one report that are kept.</summary>
    public const int MaxResults = 4096;

    /// <summary>The most errors of one document that are kept.</summary>
    public const int MaxErrorsPerResult = 16;

    /// <summary>The longest error text that is kept.</summary>
    public const int MaxErrorLength = 512;

    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);

    private readonly ConfigService _config;
    private readonly IConfigAgentSource _agents;
    private readonly IAgentGateway _gateway;
    private readonly IAgentRequestChannel _channel;
    private readonly HubDatabase _database;
    private readonly GatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ConfigRolloutService> _logger;
    private readonly ConcurrentDictionary<string, AgentWork> _work = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private readonly CancellationTokenSource _stop = new();
    private IDisposable? _gatewayRegistration;
    private IDisposable? _configRegistration;
    private volatile bool _stopping;

    /// <summary>Creates the service.</summary>
    /// <param name="config">The configuration service: generations and documents.</param>
    /// <param name="agents">The agents of the hub.</param>
    /// <param name="gateway">The agent gateway: presence and notifications.</param>
    /// <param name="channel">Sends the requests to the agents.</param>
    /// <param name="database">The hub database.</param>
    /// <param name="options">The gateway options; the frame limit decides how the documents are split.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ConfigRolloutService(
        ConfigService config,
        IConfigAgentSource agents,
        IAgentGateway gateway,
        IAgentRequestChannel channel,
        HubDatabase database,
        GatewayOptions options,
        TimeProvider time,
        ILogger<ConfigRolloutService> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _agents = agents;
        _gateway = gateway;
        _channel = channel;
        _database = database;
        _options = options;
        _time = time;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _configRegistration = _config.OnGenerationsChanged(OnGenerationsChanged);
        _gatewayRegistration = _gateway.Observe(this);

        // Agents that connected before this service started are handled as if they had just connected.
        foreach (AgentPresence presence in _gateway.GetAllPresence())
        {
            OnPresenceChanged(presence);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _gatewayRegistration?.Dispose();
        _configRegistration?.Dispose();
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Dispose raced with StopAsync while the host was tearing down.
        }
        Task[] running = [.. _background.Keys];
        try
        {
            await Task.WhenAll(running).WaitAsync(StopWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // What is still running ends with the process; nothing it does is needed to restart.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gatewayRegistration?.Dispose();
        _configRegistration?.Dispose();
        _stop.Dispose();
    }

    // ------------------------------------------------------------------
    // Triggers
    // ------------------------------------------------------------------

    /// <summary>An agent went online or offline, or its identity changed. A new connection of an agent that speaks <c>config_sync</c> starts a roll-out step.</summary>
    /// <param name="presence">The new presence.</param>
    public void OnPresenceChanged(AgentPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);

        if (!presence.Online || !presence.Features.Contains(HubFeatures.ConfigSync, StringComparer.Ordinal))
        {
            return;
        }

        AgentWork work = _work.GetOrAdd(presence.AgentId, static id => new AgentWork(id));
        bool newConnection;
        lock (work.Gate)
        {
            newConnection = work.EnsuredConnection != presence.ConnectionId;
            work.EnsuredConnection = presence.ConnectionId;
        }

        // A new connection recomputes first: the agent may be new, or its tags may have changed while it was offline.
        Schedule(presence.AgentId, ensure: newConnection);
    }

    /// <summary>An agent reported the outcome of a generation, or the generation it has after a handshake.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="connectionId">The connection it arrived on.</param>
    /// <param name="status">The report.</param>
    public void OnConfigStatus(string agentId, long connectionId, ConfigStatusMessage status)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(status);

        Schedule(agentId, status: status);
    }

    /// <summary>An administrator removed the agent: what the hub recorded about its roll-out goes with it.</summary>
    /// <param name="agentId">The agent.</param>
    public void OnAgentRemoved(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        _ = _work.TryRemove(agentId, out _);
        Track(Task.Run(() => ForgetAsync(agentId), CancellationToken.None));
    }

    /// <summary>
    /// The tags of an agent changed: recomputes its desired state, which creates a new generation when the content differs, and pushes it when the agent is online.
    /// </summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The change, or null when the content did not change or there is no such agent.</returns>
    public async Task<AgentChange?> OnAgentTagsChangedAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        // The configuration service announces a new generation, which schedules the push.
        return await _config.RecomputeAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks for another roll-out step for an agent: recomputes its desired state and sends it, whatever was sent before. Does nothing while the agent is offline.</summary>
    /// <param name="agentId">The agent.</param>
    public void RequestSync(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        Schedule(agentId, ensure: true, force: true);
    }

    private void OnGenerationsChanged(IReadOnlyList<string> agentIds)
    {
        foreach (string agentId in agentIds)
        {
            Schedule(agentId);
        }
    }

    // ------------------------------------------------------------------
    // The work of one agent
    // ------------------------------------------------------------------

    private void Schedule(string agentId, bool ensure = false, bool force = false, ConfigStatusMessage? status = null)
    {
        if (_stopping)
        {
            return;
        }

        AgentWork work = _work.GetOrAdd(agentId, static id => new AgentWork(id));
        bool start;
        lock (work.Gate)
        {
            work.Dirty = true;
            work.Ensure |= ensure;
            work.Force |= force;
            if (status is not null)
            {
                // Only the newest report counts; it replaces an older one that has not been stored yet.
                work.Status = status;
            }

            start = !work.Running;
            work.Running = true;
        }

        if (start)
        {
            Track(Task.Run(() => PumpAsync(work), CancellationToken.None));
        }
    }

    private void Track(Task task)
    {
        _ = _background.TryAdd(task, 0);
        _ = task.ContinueWith(
            static (finished, state) => ((ConfigRolloutService)state!)._background.TryRemove(finished, out _),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Works through what has been scheduled for one agent, one step after the other. A change that comes meanwhile marks the work dirty and the loop runs again, so the
    /// agent ends at the newest generation and never gets more than one push for several changes that arrive together.
    /// </summary>
    private async Task PumpAsync(AgentWork work)
    {
        bool released = false;
        try
        {
            while (true)
            {
                ConfigStatusMessage? status;
                bool ensure;
                bool force;
                lock (work.Gate)
                {
                    if (!work.Dirty || _stopping)
                    {
                        work.Dirty = false;
                        work.Running = false;
                        released = true;
                        return;
                    }

                    work.Dirty = false;
                    status = work.Status;
                    work.Status = null;
                    ensure = work.Ensure;
                    work.Ensure = false;
                    force = work.Force;
                    work.Force = false;
                }

                await StepAsync(work, status, ensure, force, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The hub is stopping.
        }
        finally
        {
            if (!released)
            {
                lock (work.Gate)
                {
                    work.Running = false;
                }
            }
        }
    }

    private async Task StepAsync(AgentWork work, ConfigStatusMessage? status, bool ensure, bool force, CancellationToken cancellationToken)
    {
        try
        {
            if (status is not null)
            {
                await RecordStatusAsync(work.AgentId, status, cancellationToken).ConfigureAwait(false);
            }

            if (ensure)
            {
                _ = await _config.RecomputeAgentAsync(work.AgentId, cancellationToken).ConfigureAwait(false);
            }

            await ReconcileAsync(work, force, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException or JsonException)
        {
            RolloutLog.Failed(_logger, work.AgentId, exception.GetType().Name);
        }
    }

    private async Task ReconcileAsync(AgentWork work, bool force, CancellationToken cancellationToken)
    {
        string agentId = work.AgentId;
        AgentPresence presence = _gateway.GetPresence(agentId);
        if (!presence.Online || !presence.Features.Contains(HubFeatures.ConfigSync, StringComparer.Ordinal))
        {
            return;
        }

        AgentGeneration? generation = await _config.GetGenerationAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            // No profile has ever reached this agent: there is nothing to send.
            return;
        }

        if (!force)
        {
            AgentConfigRow? row = await _database.QueryAsync(connection => RolloutStorage.Find(connection, null, agentId), cancellationToken).ConfigureAwait(false);
            if ((row?.AppliedGeneration ?? 0) >= generation.Generation)
            {
                // The agent has it; the report after its handshake corrects this should it have lost it.
                return;
            }

            lock (work.Gate)
            {
                if (work.PushedConnection == presence.ConnectionId && work.PushedGeneration >= generation.Generation)
                {
                    // Sent over this connection already.
                    return;
                }
            }
        }

        lock (work.Gate)
        {
            work.PushedConnection = presence.ConnectionId;
            work.PushedGeneration = generation.Generation;
        }

        await PushAsync(agentId, presence.ConnectionId, generation, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Pushing a generation
    // ------------------------------------------------------------------

    private async Task PushAsync(string agentId, long connection, AgentGeneration generation, CancellationToken cancellationToken)
    {
        AgentRequestOutcome desired = await _channel.RequestAsync(
            agentId,
            connection,
            id => Serialize(generation.ToMessage(id)),
            cancellationToken).ConfigureAwait(false);
        if (!desired.Ok)
        {
            await FailPushAsync(agentId, generation, Describe("config_desired", desired), cancellationToken).ConfigureAwait(false);
            return;
        }

        DocumentRef[]? need = ReadNeed(desired.Data);
        if (need is null)
        {
            await FailPushAsync(agentId, generation, "The agent answered config_desired with a result that is not valid.", cancellationToken).ConfigureAwait(false);
            return;
        }

        HashSet<(string Kind, string Id)> wanted = [.. need.Select(static reference => (reference.Kind, reference.Id))];
        DesiredItem[] items = [.. generation.Items.Where(item => wanted.Contains((item.Kind, item.Id)))];
        if (items.Length == 0)
        {
            // The agent carries every document of the generation: no body travels.
            await RecordPushAsync(agentId, generation, null, cancellationToken).ConfigureAwait(false);
            RolloutLog.Pushed(_logger, agentId, generation.Generation, 0, 0);
            return;
        }

        IReadOnlyList<HubDocument> documents = await _config.LoadDocumentsAsync(items, cancellationToken).ConfigureAwait(false);
        if (documents.Count < items.Length)
        {
            string missing = string.Join(
                ", ",
                items.Where(item => !documents.Any(document => document.Kind == item.Kind && document.Id == item.Id)).Select(static item => item.Kind + "/" + item.Id));
            await FailPushAsync(agentId, generation, "The hub does not hold the body of " + missing + ".", cancellationToken).ConfigureAwait(false);
            return;
        }

        DocumentChunks plan = DocumentChunker.Split(generation.Generation, documents, _options.MaxFrameBytes);
        if (plan.TooLarge is { } tooLarge)
        {
            await FailPushAsync(
                agentId,
                generation,
                "The document " + tooLarge.Kind + "/" + tooLarge.Id + " is larger than a frame of the agent link may be.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        for (int index = 0; index < plan.Chunks.Count; index++)
        {
            bool last = index == plan.Chunks.Count - 1;
            IReadOnlyList<HubDocument> chunk = plan.Chunks[index];
            AgentRequestOutcome sent = await _channel.RequestAsync(
                agentId,
                connection,
                id => Serialize(new ConfigDocumentsMessage(id, generation.Generation, chunk, last)),
                cancellationToken).ConfigureAwait(false);
            if (!sent.Ok)
            {
                await FailPushAsync(agentId, generation, Describe("config_documents", sent), cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await RecordPushAsync(agentId, generation, null, cancellationToken).ConfigureAwait(false);
        RolloutLog.Pushed(_logger, agentId, generation.Generation, documents.Count, plan.Chunks.Count);
    }

    private async Task FailPushAsync(string agentId, AgentGeneration generation, string reason, CancellationToken cancellationToken)
    {
        RolloutLog.PushFailed(_logger, agentId, generation.Generation, reason);
        await RecordPushAsync(agentId, generation, reason, cancellationToken).ConfigureAwait(false);
    }

    private Task<int> RecordPushAsync(string agentId, AgentGeneration generation, string? error, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                AgentConfigRow row = RolloutStorage.Find(connection, transaction, agentId) ?? AgentConfigRow.Empty(agentId);
                RolloutStorage.Save(
                    connection,
                    transaction,
                    row with { PushedGeneration = generation.Generation, PushedAt = now, DeliveryError = error });
                return 0;
            },
            cancellationToken);
    }

    private static string Describe(string request, AgentRequestOutcome outcome)
    {
        return "The agent did not accept " + request + ": " + outcome.ErrorCode + " (" + outcome.ErrorMessage + ").";
    }

    private static byte[] Serialize(ConfigDesiredMessage message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, HubJsonContext.Default.ConfigDesiredMessage);
    }

    private static byte[] Serialize(ConfigDocumentsMessage message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, HubJsonContext.Default.ConfigDocumentsMessage);
    }

    private static DocumentRef[]? ReadNeed(JsonElement? data)
    {
        if (data is not { } element)
        {
            return null;
        }

        try
        {
            ConfigDesiredResultData? result = element.Deserialize(HubJsonContext.Default.ConfigDesiredResultData);
            return result?.Need.ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // What the agent reports
    // ------------------------------------------------------------------

    private async Task RecordStatusAsync(string agentId, ConfigStatusMessage status, CancellationToken cancellationToken)
    {
        bool applied = string.Equals(status.State, ConfigStates.Applied, StringComparison.Ordinal);
        if (status.Generation < 0 || !(applied || string.Equals(status.State, ConfigStates.Failed, StringComparison.Ordinal)))
        {
            RolloutLog.StatusIgnored(_logger, agentId, "its generation or state is not valid");
            return;
        }

        IReadOnlyList<ConfigItemResult> results = LimitResults(status.Results);
        DateTimeOffset now = _time.GetUtcNow();
        _ = await _database.InTransactionAsync(
            (connection, transaction) =>
            {
                AgentConfigRow row = RolloutStorage.Find(connection, transaction, agentId) ?? AgentConfigRow.Empty(agentId);
                RolloutStorage.Save(connection, transaction, ApplyStatus(row, status, results, now));
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
        RolloutLog.StatusReceived(_logger, agentId, status.Generation, status.State);
    }

    /// <summary>Folds a report into what the hub knows. An <c>applied</c> report moves the applied generation; a report of an older applied generation (the one after a handshake) does not wipe the record of a newer generation that failed.</summary>
    internal static AgentConfigRow ApplyStatus(AgentConfigRow row, ConfigStatusMessage status, IReadOnlyList<ConfigItemResult> results, DateTimeOffset now)
    {
        bool applied = string.Equals(status.State, ConfigStates.Applied, StringComparison.Ordinal);
        bool keepFailure = applied
            && string.Equals(row.ReportedState, ConfigStates.Failed, StringComparison.Ordinal)
            && status.Generation < row.ReportedGeneration;

        AgentConfigRow updated = row;
        if (applied)
        {
            updated = updated with { AppliedGeneration = status.Generation, AppliedAt = now, AppliedResults = results };
        }

        if (!keepFailure)
        {
            updated = updated with { ReportedGeneration = status.Generation, ReportedState = status.State, ReportedAt = now, ReportedResults = results };
        }

        return updated;
    }

    private static List<ConfigItemResult> LimitResults(IReadOnlyList<ConfigItemResult> results)
    {
        var limited = new List<ConfigItemResult>(Math.Min(results.Count, MaxResults));
        foreach (ConfigItemResult result in results)
        {
            if (limited.Count >= MaxResults)
            {
                break;
            }

            string[]? errors = null;
            if (result.Errors is { Count: > 0 })
            {
                errors = [.. result.Errors.Take(MaxErrorsPerResult).Select(static error => Truncate(error, MaxErrorLength))];
            }

            limited.Add(new ConfigItemResult(Truncate(result.Kind, 64), Truncate(result.Id, 128), Truncate(result.State, 32), errors));
        }

        return limited;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private async Task ForgetAsync(string agentId)
    {
        try
        {
            await _database.ExecuteAsync(connection => RolloutStorage.Delete(connection, null, agentId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            RolloutLog.Failed(_logger, agentId, exception.GetType().Name);
        }
    }

    // ------------------------------------------------------------------
    // Status for the management API
    // ------------------------------------------------------------------

    /// <summary>Shows the configuration status of one agent.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The status, or null when there is no such enrolled agent.</returns>
    public async Task<AgentConfigStatusInfo?> GetAgentStatusAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        IReadOnlyList<ConfigAgent> agents = await _agents.ListAsync(cancellationToken).ConfigureAwait(false);
        ConfigAgent? agent = agents.FirstOrDefault(candidate => string.Equals(candidate.Id, agentId, StringComparison.Ordinal));
        if (agent is null || !IsEnrolled(agent))
        {
            return null;
        }

        AgentGeneration? generation = await _config.GetGenerationAsync(agentId, cancellationToken).ConfigureAwait(false);
        AgentConfigRow? row = await _database.QueryAsync(connection => RolloutStorage.Find(connection, null, agentId), cancellationToken).ConfigureAwait(false);
        return BuildStatus(agent, generation, row, _gateway.GetPresence(agentId).Online);
    }

    /// <summary>Shows the configuration status of every enrolled agent.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agents with their status and the number of agents per state.</returns>
    public async Task<RolloutOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentConfigStatusInfo> statuses = await LoadStatusesAsync(cancellationToken).ConfigureAwait(false);
        return new RolloutOverview(Count(statuses.Select(static status => (status.State, status.Online))), statuses);
    }

    /// <summary>Shows how a published revision of a profile has reached the agents: every agent whose desired state contains documents of that revision, with its state.</summary>
    /// <param name="profileId">The profile.</param>
    /// <param name="revision">The revision, or null for the revision that is in effect.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The roll-out; not found when the profile or the revision does not exist or nothing is published.</returns>
    public async Task<ConfigResult<ProfileRolloutInfo>> GetProfileRolloutAsync(string profileId, int? revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileId);

        ProfileInfo? profile = await _config.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return ConfigResults.NotFound<ProfileRolloutInfo>("unknown_profile", "There is no such profile.");
        }

        int? effective = revision ?? profile.ActiveRevision;
        if (effective is not { } number)
        {
            return ConfigResults.NotFound<ProfileRolloutInfo>("not_published", "The profile has no published revision.");
        }

        if (await _config.GetProfileRevisionAsync(profileId, number, cancellationToken).ConfigureAwait(false) is null)
        {
            return ConfigResults.NotFound<ProfileRolloutInfo>("unknown_revision", "There is no such revision of the profile.");
        }

        IReadOnlyList<AgentConfigStatusInfo> statuses = await LoadStatusesAsync(cancellationToken).ConfigureAwait(false);
        var agents = new List<ProfileRolloutAgent>();
        foreach (AgentConfigStatusInfo status in statuses)
        {
            AgentDesiredInfo? desired = await _config.GetDesiredStateAsync(status.AgentId, cancellationToken).ConfigureAwait(false);
            if (desired is null)
            {
                continue;
            }

            DesiredItemInfo[] mine =
            [
                .. desired.Items.Where(item => string.Equals(item.Profile, profileId, StringComparison.Ordinal) && item.ProfileRevision == number),
            ];
            if (mine.Length == 0)
            {
                continue;
            }

            agents.Add(BuildProfileAgent(status, mine));
        }

        return ConfigResults.Ok(
            new ProfileRolloutInfo(profileId, number, Count(agents.Select(static agent => (agent.State, agent.Online))), agents));
    }

    private async Task<IReadOnlyList<AgentConfigStatusInfo>> LoadStatusesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ConfigAgent> agents = await _agents.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, AgentGeneration> generations = await _config.ListGenerationsAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, AgentConfigRow> rows = await _database.QueryAsync(connection => RolloutStorage.List(connection, null), cancellationToken).ConfigureAwait(false);

        var statuses = new List<AgentConfigStatusInfo>();
        foreach (ConfigAgent agent in agents.Where(IsEnrolled).OrderBy(static candidate => candidate.Id, StringComparer.Ordinal))
        {
            _ = generations.TryGetValue(agent.Id, out AgentGeneration? generation);
            AgentConfigRow? row = rows.GetValueOrDefault(agent.Id);
            statuses.Add(BuildStatus(agent, generation, row, _gateway.GetPresence(agent.Id).Online));
        }

        return statuses;
    }

    private static bool IsEnrolled(ConfigAgent agent) => string.Equals(agent.Status, AgentStatuses.Enrolled, StringComparison.Ordinal);

    internal static string StateOf(long desiredGeneration, AgentConfigRow? row)
    {
        if (desiredGeneration == 0)
        {
            return RolloutStates.NoDesiredState;
        }

        if ((row?.AppliedGeneration ?? 0) >= desiredGeneration)
        {
            return RolloutStates.UpToDate;
        }

        if (row is not null
            && string.Equals(row.ReportedState, ConfigStates.Failed, StringComparison.Ordinal)
            && row.ReportedGeneration == desiredGeneration)
        {
            return RolloutStates.Failed;
        }

        return RolloutStates.Pending;
    }

    private static AgentConfigStatusInfo BuildStatus(ConfigAgent agent, AgentGeneration? generation, AgentConfigRow? row, bool online)
    {
        long desired = generation?.Generation ?? 0;
        DocumentRollout[] documents = [.. ResultsFor(desired, row).Select(ToDocument)];
        string[] errors =
        [
            .. documents.SelectMany(static document => document.Errors.Select(error => document.Kind + "/" + document.Id + ": " + error)),
        ];
        return new AgentConfigStatusInfo(
            agent.Id,
            agent.Name,
            online,
            StateOf(desired, row),
            desired,
            row?.PushedGeneration ?? 0,
            row?.PushedAt,
            row?.DeliveryError,
            row?.AppliedGeneration ?? 0,
            row?.AppliedAt,
            row?.ReportedGeneration ?? 0,
            row?.ReportedState,
            row?.ReportedAt,
            errors,
            documents);
    }

    /// <summary>The results of the report that belongs to the desired generation: the report of that generation, or the report after which the agent holds it.</summary>
    private static IReadOnlyList<ConfigItemResult> ResultsFor(long desiredGeneration, AgentConfigRow? row)
    {
        if (row is null || desiredGeneration == 0)
        {
            return [];
        }

        if (row.ReportedGeneration == desiredGeneration)
        {
            return row.ReportedResults;
        }

        return row.AppliedGeneration == desiredGeneration ? row.AppliedResults : [];
    }

    private static DocumentRollout ToDocument(ConfigItemResult result)
    {
        return new DocumentRollout(result.Kind, result.Id, result.State, result.Errors ?? []);
    }

    private static ProfileRolloutAgent BuildProfileAgent(AgentConfigStatusInfo status, DesiredItemInfo[] items)
    {
        var documents = new List<DocumentRollout>();
        foreach (DesiredItemInfo item in items)
        {
            DocumentRollout? reported = status.Documents.FirstOrDefault(document =>
                string.Equals(document.Kind, item.Kind, StringComparison.Ordinal) && string.Equals(document.Id, item.Id, StringComparison.Ordinal));
            documents.Add(reported
                ?? new DocumentRollout(
                    item.Kind,
                    item.Id,
                    string.Equals(status.State, RolloutStates.UpToDate, StringComparison.Ordinal) ? ConfigItemStates.Applied : "pending",
                    []));
        }

        string[] errors = [.. documents.SelectMany(static document => document.Errors.Select(error => document.Kind + "/" + document.Id + ": " + error))];
        return new ProfileRolloutAgent(status.AgentId, status.AgentName, status.Online, status.State, status.DesiredGeneration, status.AppliedGeneration, documents, errors);
    }

    private static RolloutCounts Count(IEnumerable<(string State, bool Online)> entries)
    {
        int total = 0;
        int upToDate = 0;
        int pending = 0;
        int failed = 0;
        int none = 0;
        int offline = 0;
        foreach ((string state, bool online) in entries)
        {
            total++;
            if (!online)
            {
                offline++;
            }

            switch (state)
            {
                case RolloutStates.UpToDate:
                    upToDate++;
                    break;
                case RolloutStates.Failed:
                    failed++;
                    break;
                case RolloutStates.NoDesiredState:
                    none++;
                    break;
                default:
                    pending++;
                    break;
            }
        }

        return new RolloutCounts(total, upToDate, pending, failed, none, offline);
    }

    /// <summary>What the service keeps about one agent while it works on it. All fields are read and written under <see cref="Gate"/>.</summary>
    private sealed class AgentWork(string agentId)
    {
        public string AgentId { get; } = agentId;

        public object Gate { get; } = new();

        public bool Running { get; set; }

        public bool Dirty { get; set; }

        public bool Ensure { get; set; }

        public bool Force { get; set; }

        public ConfigStatusMessage? Status { get; set; }

        public long EnsuredConnection { get; set; }

        public long PushedConnection { get; set; }

        public long PushedGeneration { get; set; }
    }
}
