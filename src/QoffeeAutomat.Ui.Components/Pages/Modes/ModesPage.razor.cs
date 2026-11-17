using Microsoft.AspNetCore.Components;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Modes;

/// <summary>
/// The mode groups (PLAN.md T717): every group with its current mode and a control that switches it, and the way into the
/// editor. A switch runs on the agent and can take a while, so after it was accepted the page reads the state of the group
/// until the agent says that the switch is over; it reads nothing while no switch is running.
/// </summary>
public partial class ModesPage
{
    /// <summary>A group with this many modes or fewer is switched with one button per mode, a larger one with a select.</summary>
    private const int SegmentedLimit = 4;

    private readonly List<ModeGroupItem> _groups = [];
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private ApiException? _listError;
    private ApiException? _actionError;
    private string _actionFailure = string.Empty;
    private string? _notice;
    private bool _switchBusy;
    private bool _loading = true;
    private bool _polling;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private IDialogService Dialogs { get; set; } = null!;

    [Inject]
    private ModeOptions Options { get; set; } = null!;

    [Inject]
    private TimeProvider Time { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _loading = false;
    }

    /// <inheritdoc />
    protected override async ValueTask OnDisposeAsync()
    {
        await _stop.CancelAsync();
        _stop.Dispose();
    }

    private static string EditHref(ModeGroupDocument group) => "/modes/" + Uri.EscapeDataString(group.Id);

    private static string Aria(string action, ModeGroupDocument group) => action + ": " + group.Name;

    /// <summary>The name of a mode of a group; the id when the group has no such mode (any more), empty for none.</summary>
    private static string ModeName(ModeGroupDocument group, string? modeId)
    {
        if (modeId is null)
        {
            return string.Empty;
        }

        ModeDefinition? mode = group.Modes.FirstOrDefault(candidate => string.Equals(candidate.Id, modeId, StringComparison.Ordinal));
        return mode is null || string.IsNullOrWhiteSpace(mode.Name) ? modeId : mode.Name;
    }

    private bool IsBusy(ModeGroupDocument group) => _busy.Contains(group.Id);

    private async Task ReloadAsync()
    {
        _loading = true;
        await LoadAsync();
        _loading = false;
    }

    private async Task LoadAsync()
    {
        _listError = null;
        try
        {
            ModeGroupList list = await Api.GetModeGroupsAsync();
            _groups.Clear();
            _groups.AddRange(list.Groups.OrderBy(item => item.Group.Id, StringComparer.Ordinal));
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
            return;
        }
        catch (ApiException exception)
        {
            _listError = exception;
            return;
        }

        // A switch that was started elsewhere (Home Assistant, another browser) is followed as well.
        EnsurePolling();
    }

    private void Apply(ModeState state)
    {
        int index = _groups.FindIndex(item => string.Equals(item.Group.Id, state.GroupId, StringComparison.Ordinal));
        if (index >= 0)
        {
            _groups[index] = _groups[index] with { State = state };
        }
    }

    private async Task SwitchAsync(string groupId, string mode)
    {
        ModeGroupItem? item = _groups.Find(candidate => string.Equals(candidate.Group.Id, groupId, StringComparison.Ordinal));
        if (item is null || item.State.Switching || !item.Group.Enabled || string.IsNullOrEmpty(mode) || !_busy.Add(groupId))
        {
            return;
        }

        _actionError = null;
        _notice = null;
        _switchBusy = false;
        try
        {
            await Api.SwitchModeAsync(groupId, mode);
            if (IsDisposed)
            {
                return;
            }

            // Accepted: the switch is running. The state that the agent reports takes over at the next read.
            Apply(item.State with { Switching = true, Target = mode });
            EnsurePolling();
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception) when (exception.Code == "busy")
        {
            // Another switch of this group is running (started from Home Assistant, say): show what it is doing.
            _switchBusy = true;
            await RefreshAsync(groupId);
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L.Format("Modes.SwitchFailed", item.Group.Name);
        }
        finally
        {
            _busy.Remove(groupId);
        }
    }

    private async Task RefreshAsync(string groupId)
    {
        try
        {
            Apply(await Api.GetModeStateAsync(groupId));
            EnsurePolling();
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException)
        {
            // The state stays as it was; the busy notice already says what is going on.
        }
    }

    /// <summary>Starts the loop that follows the running switches, unless it runs already. Must be called on the renderer's thread.</summary>
    private void EnsurePolling()
    {
        if (_polling || IsDisposed || !_groups.Exists(item => item.State.Switching))
        {
            return;
        }

        _polling = true;
        _ = PollAsync(_stop.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(Options.PollInterval, Time, cancellationToken);

                // InvokeAsync hands back no result, so the answer of the round is taken from a local.
                bool again = false;
                await InvokeAsync(async () => again = await PollOnceAsync(cancellationToken));
                if (!again)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left.
        }
    }

    /// <summary>Reads the state of every group that is switching. Returns false, and ends the loop, when none is.</summary>
    private async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            _polling = false;
            return false;
        }

        foreach (string id in _groups.Where(item => item.State.Switching).Select(item => item.Group.Id).ToArray())
        {
            try
            {
                Apply(await Api.GetModeStateAsync(id, cancellationToken));
            }
            catch (ApiException exception) when (exception.IsUnauthorized)
            {
                Session.MarkSignedOut();
                _polling = false;
                return false;
            }
            catch (ApiException exception) when (exception.StatusCode == 404)
            {
                // The group was deleted meanwhile.
                _groups.RemoveAll(item => string.Equals(item.Group.Id, id, StringComparison.Ordinal));
            }
            catch (ApiException)
            {
                // The agent could not be reached for a moment; the next round asks again.
            }
        }

        if (!IsDisposed)
        {
            StateHasChanged();
        }

        if (_groups.Exists(item => item.State.Switching))
        {
            return true;
        }

        _polling = false;
        return false;
    }

    private async Task DeleteAsync(ModeGroupDocument group)
    {
        bool? confirmed = await Dialogs.ShowMessageBox(
            L["Modes.Delete.Title"],
            L.Format("Modes.Delete.Message", group.Name, group.Id),
            yesText: L["Modes.Delete"],
            cancelText: L["Common.Cancel"]);
        if (confirmed != true || IsDisposed || !_busy.Add(group.Id))
        {
            return;
        }

        _actionError = null;
        _notice = null;
        _switchBusy = false;
        try
        {
            await Api.DeleteModeGroupAsync(group.Id);
            _groups.RemoveAll(existing => string.Equals(existing.Group.Id, group.Id, StringComparison.Ordinal));
            _notice = L.Format("Modes.Deleted", group.Name);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L.Format("Modes.DeleteFailed", group.Name);
        }
        finally
        {
            _busy.Remove(group.Id);
        }
    }
}
