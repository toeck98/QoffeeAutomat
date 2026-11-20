using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Fleet.Actions;

/// <summary>Who started a fleet action, for the audit entry that records its end.</summary>
/// <param name="User">The user name, or <c>token:&lt;name&gt;</c> for an API token.</param>
/// <param name="Role">The role of the caller.</param>
/// <param name="AuthMethod">One of <see cref="AuthMethods"/>.</param>
/// <param name="RemoteAddress">The address of the caller.</param>
public sealed record FleetActionActor(string User, string? Role, string AuthMethod, string? RemoteAddress);

/// <summary>The outcome of starting a fleet action: the action that now runs, or why it was refused.</summary>
/// <param name="Action">The action as it was when it started; null when refused.</param>
/// <param name="Status">The HTTP status of the refusal.</param>
/// <param name="Code">The machine-readable code of the refusal.</param>
/// <param name="Path">A JSON pointer to the offending member of the request for a validation failure; null otherwise.</param>
/// <param name="Message">What is wrong.</param>
public sealed record FleetActionStartResult(FleetActionInfo? Action, int Status, string? Code, string? Path, string? Message)
{
    /// <summary>Creates the result of an action that started.</summary>
    /// <param name="action">The action.</param>
    /// <returns>The result.</returns>
    public static FleetActionStartResult Started(FleetActionInfo action) => new(action, 202, null, null, null);

    /// <summary>Creates a validation failure (422).</summary>
    /// <param name="path">The JSON pointer.</param>
    /// <param name="code">The code.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The result.</returns>
    public static FleetActionStartResult Invalid(string path, string code, string message) => new(null, 422, code, path, message);

    /// <summary>Creates a refusal that is not a validation failure.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="code">The code.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The result.</returns>
    public static FleetActionStartResult Refused(int status, string code, string message) => new(null, status, code, null, message);
}

/// <summary>What <see cref="FleetActionService.Cancel"/> did.</summary>
public enum FleetActionCancelResult
{
    /// <summary>The action was told to stop.</summary>
    Cancelled,

    /// <summary>There is no such action.</summary>
    NotFound,

    /// <summary>The action has ended already.</summary>
    NotRunning,
}

/// <summary>
/// Fleet actions (PLAN.md S20 "Fleet actions", T917): a bulk run of one command on many agents with bounded parallelism, and the update of many agents one after another. Both start
/// in the background and are followed through <see cref="List"/> and <see cref="Find"/>; each ends with an entry in the audit log that names the user, the agents and the result.
/// A bulk run reports for every targeted agent success, failure or offline. An update runs the built-in command <c>agent_update</c> on one agent, waits until that agent is connected
/// again with the target version, and only then goes on to the next; the first agent that does not come back in time stops the update, the agents after it are not touched, and the
/// record says which agents were updated. The hub makes no request to the internet: the newest version is what the agents report as <c>agent.latest_version</c>.
/// </summary>
public sealed class FleetActionService : IAsyncDisposable
{
    /// <summary>The id of the command that updates an agent (T704).</summary>
    public const string UpdateCommand = "agent_update";

    /// <summary>The id of the metric in which an agent reports the newest release that it knows.</summary>
    public const string LatestVersionMetric = "agent.latest_version";

    /// <summary>The longest command id that is accepted.</summary>
    public const int MaxCommandLength = 256;

    private readonly AgentRegistry _registry;
    private readonly IAgentGateway _gateway;
    private readonly AuditLog _audit;
    private readonly FleetActionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FleetActionService> _logger;
    private readonly object _gate = new();
    private readonly List<ActionRun> _runs = [];
    private bool _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="registry">The agent registry.</param>
    /// <param name="gateway">The agent gateway.</param>
    /// <param name="audit">The audit log.</param>
    /// <param name="options">Limits and timings.</param>
    /// <param name="time">The clock that stamps the records.</param>
    /// <param name="logger">The logger.</param>
    public FleetActionService(
        AgentRegistry registry,
        IAgentGateway gateway,
        AuditLog audit,
        FleetActionOptions options,
        TimeProvider time,
        ILogger<FleetActionService> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        _registry = registry;
        _gateway = gateway;
        _audit = audit;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>The limits and timings in effect.</summary>
    public FleetActionOptions Options => _options;

    // ------------------------------------------------------------------
    // Starting
    // ------------------------------------------------------------------

    /// <summary>Starts a bulk run.</summary>
    /// <param name="actor">Who asks.</param>
    /// <param name="request">The command, the variables and the targets.</param>
    /// <param name="cancellationToken">Cancels the preparation; the run itself goes on in the background.</param>
    /// <returns>The action that runs, or why it was refused.</returns>
    public async Task<FleetActionStartResult> StartBulkRunAsync(FleetActionActor actor, StartBulkRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);

        string command = request.Command?.Trim() ?? string.Empty;
        if (command.Length == 0 || command.Length > MaxCommandLength)
        {
            return FleetActionStartResult.Invalid("/command", "invalid", "command must be the id of a command, at most " + MaxCommandLength.ToString(CultureInfo.InvariantCulture) + " characters.");
        }

        int parallelism = request.Parallelism ?? _options.DefaultParallelism;
        if (parallelism < 1 || parallelism > _options.MaxParallelism)
        {
            return FleetActionStartResult.Invalid("/parallelism", "invalid", "parallelism must be a number from 1 to " + _options.MaxParallelism.ToString(CultureInfo.InvariantCulture) + ".");
        }

        if (!TryTimeout(request.TimeoutSeconds, _options.DefaultRunTimeout, out TimeSpan timeout))
        {
            return FleetActionStartResult.Invalid("/timeoutSeconds", "invalid", "timeoutSeconds must be a number from 1 to " + MaxTimeoutSeconds().ToString(CultureInfo.InvariantCulture) + ".");
        }

        TargetSelection selection = await SelectAsync(request.Agents, request.Tags, cancellationToken).ConfigureAwait(false);
        if (selection.Failure is not null)
        {
            return selection.Failure;
        }

        var run = new ActionRun(FleetActionKinds.BulkRun, actor, _time, selection.Agents, selection.Tags)
        {
            Command = command,
        };
        FleetActionStartResult? refused = Register(run, singleton: false);
        if (refused is not null)
        {
            run.Dispose();
            return refused;
        }

        bool wait = request.Wait ?? true;
        IReadOnlyDictionary<string, JsonElement>? vars = request.Vars;
        run.Completion = Task.Run(() => ExecuteBulkAsync(run, vars, wait, parallelism, timeout), CancellationToken.None);
        return FleetActionStartResult.Started(run.Snapshot());
    }

    /// <summary>Starts the update of agents, one after another.</summary>
    /// <param name="actor">Who asks.</param>
    /// <param name="request">The targets, the target version and the timeout.</param>
    /// <param name="cancellationToken">Cancels the preparation; the update itself goes on in the background.</param>
    /// <returns>The action that runs, or why it was refused.</returns>
    public async Task<FleetActionStartResult> StartUpdateAsync(FleetActionActor actor, StartFleetUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);

        if (request.TargetVersion is not null && !AgentVersions.IsValid(request.TargetVersion))
        {
            return FleetActionStartResult.Invalid("/targetVersion", "invalid", "targetVersion must be a version such as 1.2.3.");
        }

        if (!TryTimeout(request.TimeoutSeconds, _options.DefaultUpdateTimeout, out TimeSpan timeout))
        {
            return FleetActionStartResult.Invalid("/timeoutSeconds", "invalid", "timeoutSeconds must be a number from 1 to " + MaxTimeoutSeconds().ToString(CultureInfo.InvariantCulture) + ".");
        }

        TargetSelection selection = await SelectAsync(request.Agents, request.Tags, cancellationToken).ConfigureAwait(false);
        if (selection.Failure is not null)
        {
            return selection.Failure;
        }

        string? target = request.TargetVersion?.Trim() ?? LatestVersion(selection.AllEnrolled);
        if (target is null)
        {
            return FleetActionStartResult.Invalid("/targetVersion", "unknown_latest", "No agent reports a newest version. Give targetVersion.");
        }

        var run = new ActionRun(FleetActionKinds.Update, actor, _time, selection.Agents, selection.Tags)
        {
            TargetVersion = target,
        };
        FleetActionStartResult? refused = Register(run, singleton: true);
        if (refused is not null)
        {
            run.Dispose();
            return refused;
        }

        run.Completion = Task.Run(() => ExecuteUpdateAsync(run, target, timeout), CancellationToken.None);
        return FleetActionStartResult.Started(run.Snapshot());
    }

    // ------------------------------------------------------------------
    // Reading and cancelling
    // ------------------------------------------------------------------

    /// <summary>The retained actions, the newest first.</summary>
    /// <returns>The actions.</returns>
    public IReadOnlyList<FleetActionInfo> List()
    {
        ActionRun[] runs;
        lock (_gate)
        {
            runs = [.. _runs];
        }

        Array.Reverse(runs);
        return [.. runs.OrderByDescending(run => run.CreatedAt).Select(run => run.Snapshot())];
    }

    /// <summary>Finds an action.</summary>
    /// <param name="id">The id of the action.</param>
    /// <returns>The action, or null when there is none (it may have been dropped from the list; the audit log keeps its record).</returns>
    public FleetActionInfo? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return FindRun(id)?.Snapshot();
    }

    /// <summary>Tells a running action to stop. The agents that were not handled yet stay untouched; a command that already runs on an agent is not taken back.</summary>
    /// <param name="id">The id of the action.</param>
    /// <returns>What happened.</returns>
    public FleetActionCancelResult Cancel(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        ActionRun? run = FindRun(id);
        if (run is null)
        {
            return FleetActionCancelResult.NotFound;
        }

        return run.RequestCancel() ? FleetActionCancelResult.Cancelled : FleetActionCancelResult.NotRunning;
    }

    /// <summary>Waits until an action has ended. Tests and the shutdown use it; callers of the API poll.</summary>
    /// <param name="id">The id of the action.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The ended action, or null when there is no such action.</returns>
    public async Task<FleetActionInfo?> WaitForEndAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        ActionRun? run = FindRun(id);
        if (run is null)
        {
            return null;
        }

        await run.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        return run.Snapshot();
    }

    /// <summary>Shows the version of every enrolled agent against the newest release that an agent reports.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The overview.</returns>
    public async Task<FleetVersionOverview> GetVersionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentRecord> agents = await _registry.ListAsync(AgentStatuses.Enrolled, cancellationToken).ConfigureAwait(false);
        string? latest = LatestVersion(agents);
        AgentVersionInfo[] infos =
        [
            .. agents
                .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(agent => agent.Id, StringComparer.Ordinal)
                .Select(agent => ToVersionInfo(agent, latest)),
        ];
        return new FleetVersionOverview(latest, infos);
    }

    // ------------------------------------------------------------------
    // Bulk run
    // ------------------------------------------------------------------

    private async Task ExecuteBulkAsync(ActionRun run, IReadOnlyDictionary<string, JsonElement>? vars, bool wait, int parallelism, TimeSpan timeout)
    {
        try
        {
            using var slots = new SemaphoreSlim(parallelism);
            await Task.WhenAll(run.Items.Select(item => RunBulkItemAsync(run, item, vars, wait, timeout, slots))).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            FleetActionLog.Fault(_logger, run.Id, exception.GetType().Name);
            run.FailUnfinished("internal_error", "The hub failed while it ran the command.");
        }

        await FinishAsync(run).ConfigureAwait(false);
    }

    private async Task RunBulkItemAsync(
        ActionRun run,
        RunItem item,
        IReadOnlyDictionary<string, JsonElement>? vars,
        bool wait,
        TimeSpan timeout,
        SemaphoreSlim slots)
    {
        CancellationToken token = run.Token;
        try
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            run.Mark(item, FleetActionItemStates.Cancelled, null, "Cancelled before the command was sent.");
            return;
        }

        try
        {
            if (token.IsCancellationRequested)
            {
                run.Mark(item, FleetActionItemStates.Cancelled, null, "Cancelled before the command was sent.");
                return;
            }

            if (!_gateway.GetPresence(item.AgentId).Online)
            {
                run.Mark(item, FleetActionItemStates.Offline, HubErrorCodes.AgentOffline, "The agent is not connected.", started: false);
                return;
            }

            run.Begin(item);
            long started = Stopwatch.GetTimestamp();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            AgentRunOutcome outcome;
            try
            {
                outcome = await _gateway.RunAsync(new AgentRunRequest(item.AgentId, run.Command!, vars, wait, run.Actor.User), limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                run.Mark(item, FleetActionItemStates.Failure, ErrorCodes.Timeout, "The command did not end within " + Seconds(timeout) + " s.", durationMs: Elapsed(started));
                return;
            }
            catch (OperationCanceledException)
            {
                run.Mark(item, FleetActionItemStates.Cancelled, null, "Cancelled while the command ran; it may still finish on the agent.", durationMs: Elapsed(started));
                return;
            }

            ClassifyRun(run, item, outcome, wait, Elapsed(started));
        }
        finally
        {
            _ = slots.Release();
        }
    }

    private static void ClassifyRun(ActionRun run, RunItem item, AgentRunOutcome outcome, bool wait, long durationMs)
    {
        if (!outcome.Ok)
        {
            string code = outcome.ErrorCode ?? "internal";
            string message = outcome.ErrorMessage ?? "The command could not be run.";
            run.Mark(
                item,
                code == HubErrorCodes.AgentOffline ? FleetActionItemStates.Offline : FleetActionItemStates.Failure,
                code,
                message,
                durationMs: durationMs);
            return;
        }

        if (!wait || outcome.Result is not { } result)
        {
            run.Mark(item, FleetActionItemStates.Success, null, "The agent started the command.", durationMs: durationMs);
            return;
        }

        long? reported = result.DurationMs;
        if (result.TimedOut == true)
        {
            run.Mark(item, FleetActionItemStates.Failure, ErrorCodes.Timeout, "The command exceeded its timeout on the agent.", result.ExitCode, reported ?? durationMs);
        }
        else if (result.ExitCode is { } exit and not 0)
        {
            run.Mark(item, FleetActionItemStates.Failure, "nonzero_exit", "The command ended with exit code " + exit.ToString(CultureInfo.InvariantCulture) + ".", exit, reported ?? durationMs);
        }
        else
        {
            run.Mark(item, FleetActionItemStates.Success, null, null, result.ExitCode, reported ?? durationMs);
        }
    }

    // ------------------------------------------------------------------
    // Update orchestration
    // ------------------------------------------------------------------

    private async Task ExecuteUpdateAsync(ActionRun run, string target, TimeSpan timeout)
    {
        try
        {
            await UpdateInOrderAsync(run, target, timeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            FleetActionLog.Fault(_logger, run.Id, exception.GetType().Name);
            run.FailUnfinished("internal_error", "The hub failed while it updated the agents.");
        }

        await FinishAsync(run).ConfigureAwait(false);
    }

    private async Task UpdateInOrderAsync(ActionRun run, string target, TimeSpan timeout)
    {
        CancellationToken token = run.Token;
        foreach (RunItem item in run.Items)
        {
            if (token.IsCancellationRequested)
            {
                run.Mark(item, FleetActionItemStates.Cancelled, null, "Cancelled before this agent was updated.");
                continue;
            }

            if (run.StoppedAt is not null)
            {
                run.Mark(item, FleetActionItemStates.Skipped, "not_reached", "Not updated: an earlier agent did not come back.");
                continue;
            }

            AgentPresence presence = _gateway.GetPresence(item.AgentId);
            string? before = presence.Agent?.Version ?? item.KnownVersion;
            if (!presence.Online)
            {
                run.Mark(item, FleetActionItemStates.Offline, HubErrorCodes.AgentOffline, "The agent is not connected, so it was not updated.", started: false, versionBefore: before);
                continue;
            }

            if (AgentVersions.AtLeast(before, target))
            {
                run.Mark(item, FleetActionItemStates.Skipped, "already_current", "The agent has version " + before + " already.", started: false, versionBefore: before);
                continue;
            }

            await UpdateOneAsync(run, item, presence, before, target, timeout).ConfigureAwait(false);
        }
    }

    private async Task UpdateOneAsync(ActionRun run, RunItem item, AgentPresence presence, string? before, string target, TimeSpan timeout)
    {
        CancellationToken token = run.Token;
        run.Begin(item, before);
        long started = Stopwatch.GetTimestamp();
        long connection = presence.ConnectionId;

        // The command does not wait for its end: the agent says bye and the installer replaces it while the run would still be open.
        AgentRunOutcome outcome;
        try
        {
            outcome = await _gateway.RunAsync(new AgentRunRequest(item.AgentId, UpdateCommand, null, false, run.Actor.User), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            run.Mark(item, FleetActionItemStates.Cancelled, null, "Cancelled before the agent answered.", durationMs: Elapsed(started), versionBefore: before);
            return;
        }

        if (!outcome.Ok)
        {
            string code = outcome.ErrorCode ?? "internal";
            string message = outcome.ErrorMessage ?? "The agent refused the update.";
            if (code == HubErrorCodes.AgentOffline)
            {
                run.Mark(item, FleetActionItemStates.Offline, code, "The agent went offline before the update started.", durationMs: Elapsed(started), versionBefore: before);
                return;
            }

            run.Mark(item, FleetActionItemStates.Failure, code, message, durationMs: Elapsed(started), versionBefore: before);
            Stop(run, item, message);
            return;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        string? seen = before;
        try
        {
            while (true)
            {
                await Task.Delay(_options.PollInterval, limit.Token).ConfigureAwait(false);
                AgentPresence now = _gateway.GetPresence(item.AgentId);
                seen = now.Agent?.Version ?? seen;
                if (now.Online && now.ConnectionId != connection && AgentVersions.AtLeast(now.Agent?.Version, target))
                {
                    run.Mark(item, FleetActionItemStates.Success, null, "The agent came back with version " + now.Agent!.Version + ".", durationMs: Elapsed(started), versionBefore: before, versionAfter: now.Agent.Version);
                    run.AddUpdated(item.AgentId);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            run.Mark(item, FleetActionItemStates.Cancelled, null, "Cancelled while waiting for the agent; its update may still finish.", durationMs: Elapsed(started), versionBefore: before, versionAfter: seen);
        }
        catch (OperationCanceledException)
        {
            string reason = "The agent did not come back with version " + target + " within " + Seconds(timeout) + " s (last version seen: " + (seen ?? "unknown") + ").";
            run.Mark(item, FleetActionItemStates.Failure, "version_not_reached", reason, durationMs: Elapsed(started), versionBefore: before, versionAfter: seen);
            Stop(run, item, reason);
        }
    }

    private void Stop(ActionRun run, RunItem item, string reason)
    {
        run.Stop(item.AgentId, reason);
        FleetActionLog.UpdateStopped(_logger, run.Id, item.AgentId, reason);
    }

    // ------------------------------------------------------------------
    // The end of an action
    // ------------------------------------------------------------------

    private async Task FinishAsync(ActionRun run)
    {
        FleetActionInfo finished = run.Finish();
        FleetActionLog.Ended(_logger, run.Id, finished.State);

        // The audit middleware wrote the entry of the request that started the action; this is the record of how it ended, with the agent of every result.
        bool ok = finished.State == FleetActionStates.Completed
            && finished.Counts.Failure == 0
            && (finished.Kind == FleetActionKinds.Update || finished.Counts.Offline == 0);
        string target = "fleet-action:" + run.Id + (finished.Command is null ? ":update:" + finished.TargetVersion : ":command:" + finished.Command);
        var entry = new NewAuditEntry(
            run.Actor.User,
            run.Actor.Role,
            run.Actor.AuthMethod,
            finished.Kind == FleetActionKinds.BulkRun ? "bulkRunFinished" : "fleetUpdateFinished",
            "POST",
            target,
            [.. finished.Items.Select(item => item.AgentId).Order(StringComparer.Ordinal)],
            ok ? AuditResults.Success : AuditResults.Failure,
            StatusCodes.Status200OK,
            run.Actor.RemoteAddress);
        try
        {
            await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            FleetActionLog.AuditWriteFailed(_logger, run.Id, exception.GetType().Name);
        }

    }

    // ------------------------------------------------------------------
    // Targets and registration
    // ------------------------------------------------------------------

    private async Task<TargetSelection> SelectAsync(IReadOnlyList<string>? agentIds, IReadOnlyList<string>? tagValues, CancellationToken cancellationToken)
    {
        if (!AgentTags.TryNormalize(tagValues, out IReadOnlyList<string> tags, out string? tagError))
        {
            return TargetSelection.Fail(FleetActionStartResult.Invalid("/tags", "invalid", tagError ?? "The tags are not valid."));
        }

        string[] ids = [.. (agentIds ?? []).Select(id => id?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal)];
        if (ids.Length == 0 && tags.Count == 0)
        {
            return TargetSelection.Fail(FleetActionStartResult.Invalid("/agents", "required", "Name at least one agent or one tag."));
        }

        IReadOnlyList<AgentRecord> enrolled = await _registry.ListAsync(AgentStatuses.Enrolled, cancellationToken).ConfigureAwait(false);
        var byId = enrolled.ToDictionary(agent => agent.Id, StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (!byId.ContainsKey(id))
            {
                return TargetSelection.Fail(FleetActionStartResult.Invalid("/agents", "unknown_agent", "There is no enrolled agent with the id '" + (id.Length > 64 ? id[..64] : id) + "'."));
            }
        }

        AgentRecord[] targets =
        [
            .. enrolled
                .Where(agent => ids.Contains(agent.Id, StringComparer.Ordinal) || (tags.Count > 0 && agent.Tags.Any(tag => tags.Contains(tag, StringComparer.Ordinal))))
                .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(agent => agent.Id, StringComparer.Ordinal),
        ];
        if (targets.Length == 0)
        {
            return TargetSelection.Fail(FleetActionStartResult.Invalid("/tags", "no_targets", "No enrolled agent carries one of the tags."));
        }

        if (targets.Length > _options.MaxAgents)
        {
            return TargetSelection.Fail(FleetActionStartResult.Invalid("/agents", "too_many", "An action may target at most " + _options.MaxAgents.ToString(CultureInfo.InvariantCulture) + " agents."));
        }

        return new TargetSelection(targets, tags, enrolled, null);
    }

    private FleetActionStartResult? Register(ActionRun run, bool singleton)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return FleetActionStartResult.Refused(StatusCodes.Status503ServiceUnavailable, "stopping", "The hub is stopping.");
            }

            int running = _runs.Count(candidate => !candidate.IsEnded);
            if (running >= _options.MaxRunningActions)
            {
                return FleetActionStartResult.Refused(StatusCodes.Status409Conflict, "too_many_actions", "As many fleet actions run as the hub allows. Wait for one to end or cancel it.");
            }

            if (singleton && _runs.Any(candidate => !candidate.IsEnded && candidate.Kind == run.Kind))
            {
                return FleetActionStartResult.Refused(StatusCodes.Status409Conflict, "update_in_progress", "An update of agents is running already. Wait for it to end or cancel it.");
            }

            _runs.Add(run);
            FleetActionLog.Started(_logger, run.Id, run.Kind, run.Actor.User, run.Items.Count);

            // Finished actions beyond the limit go, the oldest first; the audit log keeps the record.
            while (_runs.Count(candidate => candidate.IsEnded) > _options.MaxRetainedActions)
            {
                ActionRun oldest = _runs.Where(candidate => candidate.IsEnded).OrderBy(candidate => candidate.CreatedAt).First();
                _ = _runs.Remove(oldest);
                oldest.Dispose();
            }
        }

        return null;
    }

    private ActionRun? FindRun(string id)
    {
        lock (_gate)
        {
            return _runs.FirstOrDefault(run => string.Equals(run.Id, id, StringComparison.Ordinal));
        }
    }

    // ------------------------------------------------------------------
    // Versions
    // ------------------------------------------------------------------

    private string? LatestVersion(IReadOnlyList<AgentRecord> agents)
    {
        string? latest = null;
        foreach (AgentRecord agent in agents)
        {
            latest = AgentVersions.Newest(latest, ReportedLatest(agent.Id));
        }

        return latest;
    }

    private string? ReportedLatest(string agentId)
    {
        AgentStateSnapshot? state = _gateway.GetState(agentId);
        return state is not null && state.Values.TryGetValue(LatestVersionMetric, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private AgentVersionInfo ToVersionInfo(AgentRecord agent, string? latest)
    {
        AgentPresence presence = _gateway.GetPresence(agent.Id);
        string? version = presence.Agent?.Version ?? (string.IsNullOrEmpty(agent.Version) ? null : agent.Version);
        int? comparison = latest is null ? null : AgentVersions.Compare(version, latest);
        string state = comparison switch
        {
            null => "unknown",
            >= 0 => "current",
            _ => "outdated",
        };
        bool canUpdate = presence.Online && OffersUpdate(_gateway.GetManifest(agent.Id));
        return new AgentVersionInfo(agent.Id, agent.Name, presence.Online, version, agent.Tags, state, canUpdate);
    }

    private static bool OffersUpdate(CachedManifest? manifest)
    {
        if (manifest is null
            || manifest.Manifest.ValueKind != JsonValueKind.Object
            || !manifest.Manifest.TryGetProperty("commands", out JsonElement commands)
            || commands.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement command in commands.EnumerateArray())
        {
            if (command.ValueKind == JsonValueKind.Object
                && command.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() == UpdateCommand)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private int MaxTimeoutSeconds() => (int)Math.Min(int.MaxValue, _options.MaxTimeout.TotalSeconds);

    private bool TryTimeout(int? seconds, TimeSpan fallback, out TimeSpan timeout)
    {
        if (seconds is null)
        {
            timeout = fallback;
            return true;
        }

        timeout = TimeSpan.FromSeconds(seconds.Value);
        return seconds.Value >= 1 && timeout <= _options.MaxTimeout;
    }

    private static string Seconds(TimeSpan span) => Math.Max(1, (int)Math.Round(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    private static long Elapsed(long startedTimestamp) => (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        ActionRun[] runs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            runs = [.. _runs];
        }

        foreach (ActionRun run in runs)
        {
            _ = run.RequestCancel();
        }

        try
        {
            await Task.WhenAll(runs.Select(run => run.Completion)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The hub is stopping anyway.
        }

        foreach (ActionRun run in runs)
        {
            run.Dispose();
        }

    }

    private sealed record TargetSelection(IReadOnlyList<AgentRecord> Agents, IReadOnlyList<string> Tags, IReadOnlyList<AgentRecord> AllEnrolled, FleetActionStartResult? Failure)
    {
        public static TargetSelection Fail(FleetActionStartResult failure) => new([], [], [], failure);
    }

    /// <summary>One agent of a running action; changed only through <see cref="ActionRun"/>, under its lock.</summary>
    private sealed class RunItem
    {
        public RunItem(AgentRecord agent)
        {
            AgentId = agent.Id;
            AgentName = agent.Name;
            KnownVersion = string.IsNullOrEmpty(agent.Version) ? null : agent.Version;
        }

        public string AgentId { get; }

        public string AgentName { get; }

        public string? KnownVersion { get; }

        public string State { get; set; } = FleetActionItemStates.Pending;

        public string? ErrorCode { get; set; }

        public string? Message { get; set; }

        public int? ExitCode { get; set; }

        public long? DurationMs { get; set; }

        public string? VersionBefore { get; set; }

        public string? VersionAfter { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }
    }

    /// <summary>The record of one action: its agents and their states, and the token that cancels it.</summary>
    private sealed class ActionRun : IDisposable
    {
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cancel = new();
        private readonly List<string> _updated = [];
        private readonly TimeProvider _clock;
        private string _state = FleetActionStates.Running;
        private DateTimeOffset? _finishedAt;
        private string? _stoppedAt;
        private string? _stopReason;
        private bool _cancelRequested;
        private bool _disposed;

        public ActionRun(string kind, FleetActionActor actor, TimeProvider clock, IReadOnlyList<AgentRecord> agents, IReadOnlyList<string> tags)
        {
            Id = Guid.NewGuid().ToString("N");
            Kind = kind;
            Actor = actor;
            _clock = clock;
            CreatedAt = clock.GetUtcNow();
            Tags = tags;
            Items = [.. agents.Select(agent => new RunItem(agent))];
        }

        public string Id { get; }

        public string Kind { get; }

        public FleetActionActor Actor { get; }

        public DateTimeOffset CreatedAt { get; }

        public IReadOnlyList<string> Tags { get; }

        public IReadOnlyList<RunItem> Items { get; }

        public string? Command { get; init; }

        public string? TargetVersion { get; init; }

        public Task Completion { get; set; } = Task.CompletedTask;

        public CancellationToken Token => _cancel.Token;

        public string? StoppedAt
        {
            get
            {
                lock (_lock)
                {
                    return _stoppedAt;
                }
            }
        }

        public bool IsEnded
        {
            get
            {
                lock (_lock)
                {
                    return _state != FleetActionStates.Running;
                }
            }
        }

        public bool RequestCancel()
        {
            lock (_lock)
            {
                if (_state != FleetActionStates.Running || _disposed)
                {
                    return false;
                }

                _cancelRequested = true;
            }

            _cancel.Cancel();
            return true;
        }

        public void Begin(RunItem item, string? versionBefore = null)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            lock (_lock)
            {
                item.State = FleetActionItemStates.Running;
                item.StartedAt = now;
                item.VersionBefore = versionBefore ?? item.VersionBefore;
            }
        }

        public void Mark(
            RunItem item,
            string state,
            string? errorCode,
            string? message,
            int? exitCode = null,
            long? durationMs = null,
            bool started = true,
            string? versionBefore = null,
            string? versionAfter = null)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            lock (_lock)
            {
                item.State = state;
                item.ErrorCode = errorCode;
                item.Message = message;
                item.ExitCode = exitCode ?? item.ExitCode;
                item.DurationMs = durationMs ?? item.DurationMs;
                item.VersionBefore = versionBefore ?? item.VersionBefore;
                item.VersionAfter = versionAfter ?? item.VersionAfter;
                item.StartedAt ??= started ? now : null;
                item.FinishedAt = now;
            }
        }

        public void AddUpdated(string agentId)
        {
            lock (_lock)
            {
                _updated.Add(agentId);
            }
        }

        public void Stop(string agentId, string reason)
        {
            lock (_lock)
            {
                _stoppedAt ??= agentId;
                _stopReason ??= reason;
            }
        }

        public void FailUnfinished(string code, string message)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            lock (_lock)
            {
                foreach (RunItem item in Items.Where(item => item.State is FleetActionItemStates.Pending or FleetActionItemStates.Running))
                {
                    item.State = FleetActionItemStates.Failure;
                    item.ErrorCode = code;
                    item.Message = message;
                    item.FinishedAt = now;
                }
            }
        }

        public FleetActionInfo Finish()
        {
            DateTimeOffset now = _clock.GetUtcNow();
            lock (_lock)
            {
                // A cancel that came after the last agent was handled changed nothing, so the action counts as completed.
                bool cancelled = _cancelRequested && Items.Any(item => item.State == FleetActionItemStates.Cancelled);
                _state = _stoppedAt is not null
                    ? FleetActionStates.Stopped
                    : cancelled ? FleetActionStates.Cancelled : FleetActionStates.Completed;
                _finishedAt = now;
                return SnapshotLocked();
            }
        }

        public FleetActionInfo Snapshot()
        {
            lock (_lock)
            {
                return SnapshotLocked();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _cancel.Dispose();
        }

        private FleetActionInfo SnapshotLocked()
        {
            FleetActionItem[] items =
            [
                .. Items.Select(item => new FleetActionItem(
                    item.AgentId,
                    item.AgentName,
                    item.State,
                    item.ErrorCode,
                    item.Message,
                    item.ExitCode,
                    item.DurationMs,
                    item.VersionBefore,
                    item.VersionAfter,
                    item.StartedAt,
                    item.FinishedAt)),
            ];
            int Count(string state) => items.Count(item => item.State == state);
            var counts = new FleetActionCounts(
                Count(FleetActionItemStates.Pending),
                Count(FleetActionItemStates.Running),
                Count(FleetActionItemStates.Success),
                Count(FleetActionItemStates.Failure),
                Count(FleetActionItemStates.Offline),
                Count(FleetActionItemStates.Skipped),
                Count(FleetActionItemStates.Cancelled));
            return new FleetActionInfo(
                Id,
                Kind,
                _state,
                Actor.User,
                CreatedAt,
                _finishedAt,
                Command,
                TargetVersion,
                Tags,
                counts,
                items,
                [.. _updated],
                _stoppedAt,
                _stopReason);
        }
    }
}
