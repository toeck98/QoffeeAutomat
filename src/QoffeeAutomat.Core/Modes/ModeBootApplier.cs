using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Modes;

/// <summary>
/// Carries out <c>onBoot: apply_default</c> (PLAN.md S06, T717): after an operating-system start it waits until the session is ready
/// (the metric <c>session.ready</c>: a user is logged in and the session helper accepts user commands) and then runs the generated
/// command of the group for the default mode, so the switch is recorded in the history and obeys the same rules as any other. A group
/// that was switched by hand while the applier was still waiting is left alone, and so is a group that was disabled or deleted. An agent
/// restart in the same boot never gets here: <see cref="ModeEngine.ReconcileBootAsync"/> returns the groups only when the boot id changed.
/// </summary>
public sealed class ModeBootApplier
{
    private readonly ModeEngine _modes;
    private readonly CommandEngine _commands;
    private readonly ISessionBridge? _session;

    /// <summary>Creates the applier.</summary>
    /// <param name="modes">The mode engine.</param>
    /// <param name="commands">The command engine that runs the generated commands.</param>
    /// <param name="session">The session bridge; null means there is no session to wait for and the default mode is applied at once.</param>
    public ModeBootApplier(ModeEngine modes, CommandEngine commands, ISessionBridge? session)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(commands);

        _modes = modes;
        _commands = commands;
        _session = session;
    }

    /// <summary>Waits for the session and applies the default mode of every group in <paramref name="groupIds"/>; the groups are independent of each other.</summary>
    /// <param name="groupIds">The groups that <see cref="ModeEngine.ReconcileBootAsync"/> returned.</param>
    /// <param name="cancellationToken">Stops waiting (the agent is shutting down).</param>
    /// <returns>A task that completes when every group was handled.</returns>
    public async Task ApplyAsync(IReadOnlyList<string> groupIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupIds);

        if (groupIds.Count == 0)
        {
            return;
        }

        await WaitForSessionAsync(cancellationToken).ConfigureAwait(false);
        foreach (string groupId in groupIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModeGroupDocument? group = _modes.Find(groupId);
            if (group is null || !group.Enabled || !_modes.IsBootApplyPending(groupId))
            {
                continue;
            }

            var variables = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [ModeNames.Variable] = Text(group.DefaultMode),
            };
            await _commands.RunAsync(
                new RunRequest(ModeNames.CommandId(group.Id), variables, RunSources.Boot),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is null || _session.State.IsReady)
        {
            return;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(object? sender, SessionStateChangedEventArgs e)
        {
            if (e.State.IsReady)
            {
                ready.TrySetResult();
            }
        }

        _session.StateChanged += OnChanged;
        try
        {
            // The state may have become ready between the first check and the subscription.
            if (_session.State.IsReady)
            {
                return;
            }

            await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _session.StateChanged -= OnChanged;
        }
    }

    private static JsonElement Text(string value)
    {
        using JsonDocument document = JsonDocument.Parse("\"" + JsonEncodedText.Encode(value) + "\"");
        return document.RootElement.Clone();
    }
}
