using Microsoft.AspNetCore.Components;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Commands;

/// <summary>
/// The command list (PLAN.md T403): every command with search and filters, the switches for <c>enabled</c> and
/// <c>exposeToHa</c> that save at once (and spring back when the agent refuses), the badges for the action type and
/// the run context, the last run, and the actions run, edit, duplicate and delete.
/// </summary>
public partial class CommandsPage
{
    /// <summary>How many of the newest runs are read to find the last run of every command.</summary>
    private const int RunsToScan = 200;

    /// <summary>The <c>source</c> of the runs of a draft in the editor; they are not runs of the command.</summary>
    private const string TestSource = "test";

    private const int MaxCopyAttempts = 5;

    private readonly List<CommandDocument> _commands = [];
    private readonly Dictionary<string, RunSummary> _lastRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommandPatch> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private ApiException? _listError;
    private ApiException? _actionError;
    private string _actionFailure = string.Empty;
    private string? _notice;
    private bool _loading = true;
    private string? _search;
    private string? _platformOs;
    private SwitchFilter _enabledFilter;
    private SwitchFilter _exposeFilter;
    private string _actionFilter = CommandFilter.All;
    private string _runAsFilter = CommandFilter.All;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private IDialogService Dialogs { get; set; } = null!;

    private CommandFilter Filter => new()
    {
        Search = _search ?? string.Empty,
        Enabled = _enabledFilter,
        ExposeToHa = _exposeFilter,
        ActionType = _actionFilter,
        RunAs = _runAsFilter,
    };

    private IReadOnlyList<CommandDocument> Visible => Filter.Apply(_commands);

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _loading = false;
    }

    private static string EditHref(CommandDocument command) => "/commands/" + Uri.EscapeDataString(command.Id);

    private static string Aria(string action, CommandDocument command) => action + ": " + command.Name;

    private static string LastRunState(RunSummary? run)
    {
        if (run is null)
        {
            return "never";
        }

        return run.State switch
        {
            "waiting" => "waiting",
            "running" => "running",
            _ => run.Succeeded ? "succeeded" : "failed",
        };
    }

    private static Color LastRunColor(RunSummary run) => LastRunState(run) switch
    {
        "succeeded" => Color.Success,
        "failed" => Color.Error,
        _ => Color.Info,
    };

    private static Color RunAsColor(CommandDocument command) => command.RunAs switch
    {
        RunAsValues.System => Color.Secondary,
        RunAsValues.UserElevated => Color.Warning,
        _ => Color.Default,
    };

    private string ActionText(CommandDocument command) => command.Action.Type switch
    {
        ActionTypes.PowerShell => L["Commands.Action.PowerShell"],
        ActionTypes.Bash => L["Commands.Action.Bash"],
        ActionTypes.Process => L["Commands.Action.Process"],
        ActionTypes.Builtin => L["Commands.Action.Builtin"],
        ActionTypes.Sequence => L["Commands.Action.Sequence"],
        _ => command.Action.Type,
    };

    /// <summary>True when the command uses an action type that the operating system of the agent cannot run; the document is kept and flagged.</summary>
    private bool IsUnsupported(CommandDocument command) => !PlatformActionSupport.IsActionTypeSupported(_platformOs, command.Action.Type);

    private string RunAsText(CommandDocument command) => command.RunAs switch
    {
        RunAsValues.System => L["Commands.RunAs.System"],
        RunAsValues.User => L["Commands.RunAs.User"],
        RunAsValues.UserElevated => L["Commands.RunAs.UserElevated"],
        _ => command.RunAs,
    };

    private string LastRunText(RunSummary run) => LastRunState(run) switch
    {
        "succeeded" => L["Dashboard.Runs.Succeeded"],
        "failed" => L["Dashboard.Runs.Failed"],
        "running" => L["Dashboard.Runs.Running"],
        _ => L["Dashboard.Runs.Waiting"],
    };

    private RunSummary? LastRunOf(CommandDocument command) => _lastRuns.GetValueOrDefault(command.Id);

    private string LastRunState(CommandDocument command) => LastRunState(LastRunOf(command));

    private bool IsBusy(CommandDocument command) => _busy.Contains(command.Id);

    /// <summary>The state of the switch: the value that is being saved, or else the saved one.</summary>
    private bool IsEnabled(CommandDocument command)
    {
        return _pending.TryGetValue(command.Id, out CommandPatch? patch) && patch.Enabled is { } value ? value : command.Enabled;
    }

    private bool IsExposed(CommandDocument command)
    {
        return _pending.TryGetValue(command.Id, out CommandPatch? patch) && patch.ExposeToHa is { } value ? value : command.ExposeToHa;
    }

    private int VersionOf(string id) => _versions.GetValueOrDefault(id);

    /// <summary>
    /// A switch keeps the state that the user gave it even when the parent keeps passing the old value. A new key after every
    /// save makes the switch start again from the saved state, which is how a refused change springs back.
    /// </summary>
    private string EnabledKey(CommandDocument command) => "enabled-" + command.Id + "-" + VersionOf(command.Id).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string ExposeKey(CommandDocument command) => "expose-" + command.Id + "-" + VersionOf(command.Id).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void ClearFilters()
    {
        _search = null;
        _enabledFilter = SwitchFilter.Any;
        _exposeFilter = SwitchFilter.Any;
        _actionFilter = CommandFilter.All;
        _runAsFilter = CommandFilter.All;
    }

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
            CommandList list = await Api.GetCommandsAsync();
            _commands.Clear();
            _commands.AddRange(list.Commands.OrderBy(command => command.Id, StringComparer.Ordinal));
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            // The session guard sends the user to the login page.
            Session.MarkSignedOut();
            return;
        }
        catch (ApiException exception)
        {
            _listError = exception;
            return;
        }

        await LoadPlatformAsync();
        await LoadRunsAsync();
    }

    /// <summary>Reads the operating system of the agent, to flag the commands that it cannot run (T803). Without it nothing is flagged.</summary>
    private async Task LoadPlatformAsync()
    {
        try
        {
            _platformOs = (await Api.GetStatusAsync()).Platform.Os;
        }
        catch (ApiException)
        {
            // The flags are missing; the list is still usable.
        }
    }

    private async Task LoadRunsAsync()
    {
        try
        {
            RunList runs = await Api.GetRunsAsync(RunsToScan);
            _lastRuns.Clear();

            // Newest first: the first run of a command that is seen is its last one.
            foreach (RunSummary run in runs.Runs)
            {
                if (!string.Equals(run.Source, TestSource, StringComparison.Ordinal))
                {
                    _lastRuns.TryAdd(run.Command, run);
                }
            }
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException)
        {
            // The list is useful without the last runs.
        }
    }

    private Task SetEnabledAsync(CommandDocument command, bool value) => SaveSwitchAsync(command, new CommandPatch(Enabled: value));

    private Task SetExposedAsync(CommandDocument command, bool value) => SaveSwitchAsync(command, new CommandPatch(ExposeToHa: value));

    /// <summary>Saves a switch at once. The switch shows the new value while the agent answers and the answer decides.</summary>
    private async Task SaveSwitchAsync(CommandDocument command, CommandPatch patch)
    {
        if (!_busy.Add(command.Id))
        {
            return;
        }

        _actionError = null;
        _notice = null;
        _pending[command.Id] = patch;
        try
        {
            CommandDocument saved = await Api.PatchCommandAsync(command.Id, patch);
            int index = _commands.FindIndex(existing => string.Equals(existing.Id, command.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _commands[index] = saved;
            }
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            // Nothing was saved: the switch goes back to the saved state below, and the error says why.
            _actionError = exception;
            _actionFailure = L.Format("Commands.SaveFailed", command.Name);
        }
        finally
        {
            _pending.Remove(command.Id);
            _busy.Remove(command.Id);
            _versions[command.Id] = VersionOf(command.Id) + 1;
        }
    }

    private async Task RunAsync(CommandDocument command)
    {
        var parameters = new DialogParameters<RunCommandDialog> { { dialog => dialog.Command, command } };
        var options = new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseOnEscapeKey = true };
        IDialogReference reference = await Dialogs.ShowAsync<RunCommandDialog>(command.Name, parameters, options);
        await reference.Result;
        if (!IsDisposed)
        {
            // The run that was started in the dialog is the last run of the command now.
            await LoadRunsAsync();
        }
    }

    private async Task DeleteAsync(CommandDocument command)
    {
        bool? confirmed = await Dialogs.ShowMessageBox(
            L["Commands.Delete.Title"],
            L.Format("Commands.Delete.Message", command.Name, command.Id),
            yesText: L["Commands.Delete"],
            cancelText: L["Common.Cancel"]);
        if (confirmed != true || IsDisposed || !_busy.Add(command.Id))
        {
            return;
        }

        _actionError = null;
        _notice = null;
        try
        {
            await Api.DeleteCommandAsync(command.Id);
            _commands.RemoveAll(existing => string.Equals(existing.Id, command.Id, StringComparison.Ordinal));
            _lastRuns.Remove(command.Id);
            _notice = L.Format("Commands.Deleted", command.Name);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L.Format("Commands.DeleteFailed", command.Name);
        }
        finally
        {
            _busy.Remove(command.Id);
        }
    }

    private async Task DuplicateAsync(CommandDocument source)
    {
        if (!_busy.Add(source.Id))
        {
            return;
        }

        _actionError = null;
        _notice = null;
        var taken = new HashSet<string>(_commands.Select(command => command.Id), StringComparer.Ordinal);
        try
        {
            for (int attempt = 0; attempt < MaxCopyAttempts; attempt++)
            {
                CommandDocument copy = CommandCopy.Create(source, CommandCopy.NextId(source.Id, taken), L.Format("Commands.CopyName", source.Name));
                try
                {
                    CommandDocument created = await Api.CreateCommandAsync(copy);
                    _commands.Add(created);
                    _commands.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                    _notice = L.Format("Commands.Duplicated", created.Name, created.Id);
                    return;
                }
                catch (ApiException exception) when (exception.StatusCode == 409 && attempt < MaxCopyAttempts - 1)
                {
                    // Somebody else took the id in the meantime (another tab, a hub); the next one is tried.
                    taken.Add(copy.Id);
                }
            }
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L.Format("Commands.DuplicateFailed", source.Name);
        }
        finally
        {
            _busy.Remove(source.Id);
        }
    }
}
