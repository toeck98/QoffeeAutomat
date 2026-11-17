using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.CommandEditor;

/// <summary>
/// The command editor (PLAN.md T404). It owns the draft, turns it into the document that is validated (debounced, through
/// <c>/commands/validate</c>), tested and saved, and shows the problems at the fields they belong to. Saving a new command
/// stays in the editor: the address changes to the one of the saved command and the editor goes on with it.
/// </summary>
public partial class CommandEditorPage
{
    private readonly List<string> _commandIds = [];
    private CommandDraft _draft = new();
    private DraftBuild _build = new CommandDraft().Build();
    private DraftErrors _errors = DraftErrors.Empty;
    private IReadOnlyList<ApiValidationError> _serverErrors = [];
    private IReadOnlyDictionary<string, JsonElement>? _entered;
    private IReadOnlyList<MetricInfo> _metrics = [];
    private IReadOnlyList<ScriptSummary> _scripts = [];
    private IReadOnlyList<string> _secretNames = [];
    private SessionStatus? _session;
    private string _baseline = string.Empty;
    private string? _loadedId;
    private string? _notice;
    private ApiException? _loadError;
    private ApiException? _saveError;
    private ValidationState _validation;
    private CancellationTokenSource? _validationCts;
    private int _validationVersion;
    private bool _initialised;
    private bool _lookupsLoaded;
    private bool _loading = true;
    private bool _notFound;
    private bool _saving;
    private bool _dirty;

    private enum ValidationState
    {
        None = 0,
        Pending,
        Valid,
        Invalid,
        Failed,
    }

    /// <summary>The id of the command to change; <c>null</c> on the page of a new command.</summary>
    [Parameter]
    public string? Id { get; set; }

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Inject]
    private IDialogService Dialogs { get; set; } = null!;

    [Inject]
    private CommandEditorOptions Options { get; set; } = null!;

    private string TitleText => _draft.IsNew ? L["Editor.Title.New"] : L.Format("Editor.Title.Edit", _draft.Name);

    private IReadOnlyList<string> CommandChoices => _commandIds;

    private string StateName => _validation.ToString().ToLowerInvariant();

    private string StateText => _validation switch
    {
        ValidationState.Pending => L["Editor.Validation.Pending"],
        ValidationState.Valid => L["Editor.Validation.Valid"],
        ValidationState.Invalid => L["Editor.Validation.Invalid"],
        ValidationState.Failed => L["Editor.Validation.Failed"],
        _ => L["Editor.Validation.None"],
    };

    private Color StateColor => _validation switch
    {
        ValidationState.Valid => Color.Success,
        ValidationState.Invalid => Color.Warning,
        ValidationState.Failed => Color.Error,
        _ => Color.Secondary,
    };

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (_initialised)
        {
            if (string.Equals(Id, _loadedId, StringComparison.Ordinal))
            {
                return;
            }

            if (Id is not null && !_draft.IsNew && string.Equals(Id, _draft.Id, StringComparison.Ordinal))
            {
                // A new command was saved and the address moved to it: the editor goes on with the same draft.
                _loadedId = Id;
                return;
            }
        }

        _initialised = true;
        _loadedId = Id;
        _loading = true;
        await LoadAsync();
        _loading = false;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        CancelValidation();
        return ValueTask.CompletedTask;
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        await LoadAsync();
        _loading = false;
    }

    private async Task LoadAsync()
    {
        CancelValidation();
        _loadError = null;
        _saveError = null;
        _notice = null;
        _notFound = false;
        _serverErrors = [];
        _entered = null;
        _validation = ValidationState.None;

        if (Id is null)
        {
            _draft = new CommandDraft();
        }
        else
        {
            try
            {
                _draft = CommandDraft.From(await Api.GetCommandAsync(Id));
            }
            catch (ApiException exception) when (exception.IsUnauthorized)
            {
                // The session guard sends the user to the login page.
                Session.MarkSignedOut();
                return;
            }
            catch (ApiException exception) when (exception.StatusCode == 404)
            {
                _notFound = true;
                return;
            }
            catch (ApiException exception)
            {
                _loadError = exception;
                return;
            }
        }

        Rebuild();
        _baseline = Snapshot();
        _dirty = false;

        if (!_lookupsLoaded)
        {
            await LoadLookupsAsync();
        }
    }

    /// <summary>
    /// Reads what the forms offer as choices: the metrics for the state of a control, the script files, the other commands
    /// for a sequence and the session (to warn about elevation). The editor works without any of them.
    /// </summary>
    private async Task LoadLookupsAsync()
    {
        _lookupsLoaded = true;
        await Lookup(async () => _metrics = (await Api.GetMetricsAsync()).Metrics);
        await Lookup(async () => _scripts = (await Api.GetScriptsAsync()).Scripts);
        await Lookup(async () =>
        {
            CommandList list = await Api.GetCommandsAsync();
            _commandIds.Clear();
            _commandIds.AddRange(list.Commands.Select(command => command.Id).OrderBy(id => id, StringComparer.Ordinal));
        });
        await Lookup(async () => _session = (await Api.GetStatusAsync()).Session);
        await Lookup(async () => _secretNames = [.. (await Api.GetSecretsAsync()).Secrets.Select(secret => secret.Name)]);
    }

    private async Task Lookup(Func<Task> read)
    {
        try
        {
            await read();
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException)
        {
            // The choice is missing; the field can still be typed.
        }
    }

    /// <summary>Makes the document of the draft and works out whether it differs from the saved one.</summary>
    private void Rebuild()
    {
        _build = _draft.Build();
        _errors = DraftErrors.From(_build.Problems, _serverErrors);
        _dirty = !string.Equals(Snapshot(), _baseline, StringComparison.Ordinal);
    }

    private string Snapshot() => JsonSerializer.Serialize(_build.Document, ApiDocumentJsonContext.Default.CommandDocument);

    private Task OnChangedAsync()
    {
        _notice = null;
        Rebuild();
        ScheduleValidation();
        return Task.CompletedTask;
    }

    private Task OnVariablesChangedAsync(IReadOnlyDictionary<string, JsonElement> values)
    {
        _entered = values;
        return Task.CompletedTask;
    }

    private Task OnDraftRejectedAsync(IReadOnlyList<ApiValidationError> errors)
    {
        // The agent refused to run the draft: its reasons are shown at the fields, like those of the validation.
        _serverErrors = errors;
        _errors = DraftErrors.From(_build.Problems, _serverErrors);
        _validation = ValidationState.Invalid;
        return Task.CompletedTask;
    }

    private void CancelValidation()
    {
        CancellationTokenSource? source = _validationCts;
        _validationCts = null;
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished already.
        }

        source.Dispose();
    }

    /// <summary>Asks the agent to validate the draft once it has rested for a moment; a newer change cancels the wait.</summary>
    private void ScheduleValidation()
    {
        CancelValidation();
        var source = new CancellationTokenSource();
        _validationCts = source;
        int version = ++_validationVersion;
        _validation = ValidationState.Pending;
        _ = ValidateAsync(_build.Document, version, source.Token);
    }

    private async Task ValidateAsync(CommandDocument draft, int version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Options.ValidationDelay, cancellationToken);
            ValidationReport report = await Api.ValidateCommandAsync(draft, cancellationToken);
            await InvokeAsync(() =>
            {
                if (version != _validationVersion || IsDisposed)
                {
                    return;
                }

                _serverErrors = report.Errors;
                _errors = DraftErrors.From(_build.Problems, _serverErrors);
                _validation = report.Valid ? ValidationState.Valid : ValidationState.Invalid;
                StateHasChanged();
            });
        }
        catch (OperationCanceledException)
        {
            // A newer change took over, or the page was left.
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            await InvokeAsync(Session.MarkSignedOut);
        }
        catch (ApiException)
        {
            await InvokeAsync(() =>
            {
                if (version == _validationVersion && !IsDisposed)
                {
                    _validation = ValidationState.Failed;
                    StateHasChanged();
                }
            });
        }
    }

    private async Task SaveAsync()
    {
        if (_saving)
        {
            return;
        }

        _saveError = null;
        _notice = null;
        Rebuild();
        if (_build.Problems.Count > 0)
        {
            // A value that is no value: the fields say which; nothing is sent.
            return;
        }

        CancelValidation();
        _saving = true;
        bool wasNew = _draft.IsNew;
        try
        {
            CommandDocument saved = wasNew
                ? await Api.CreateCommandAsync(_build.Document)
                : await Api.ReplaceCommandAsync(_build.Document);
            if (IsDisposed)
            {
                return;
            }

            _draft.Adopt(saved);
            _serverErrors = [];
            _validation = ValidationState.None;
            Rebuild();
            _baseline = Snapshot();
            _dirty = false;
            _notice = L.Format("Editor.Saved", saved.Name);
            if (wasNew)
            {
                // The editor stays; only the address becomes the one of the command, so that a reload finds it.
                Navigation.NavigateTo("/commands/" + Uri.EscapeDataString(saved.Id), replace: true);
            }
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception) when (exception.StatusCode == 422 && exception.ValidationErrors.Count > 0)
        {
            _serverErrors = exception.ValidationErrors;
            _errors = DraftErrors.From(_build.Problems, _serverErrors);
            _validation = ValidationState.Invalid;
        }
        catch (ApiException exception) when (wasNew && exception.StatusCode == 409)
        {
            _serverErrors = [new ApiValidationError("/id", "already_exists", L["Editor.Error.already_exists"])];
            _errors = DraftErrors.From(_build.Problems, _serverErrors);
            _validation = ValidationState.Invalid;
        }
        catch (ApiException exception)
        {
            _saveError = exception;
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>Asks before the user leaves with changes that are not saved.</summary>
    private async Task OnLeavingAsync(LocationChangingContext context)
    {
        if (!_dirty)
        {
            return;
        }

        bool? leave = await Dialogs.ShowMessageBox(
            L["Editor.Unsaved.Title"],
            L["Editor.Unsaved.Message"],
            yesText: L["Editor.Unsaved.Leave"],
            cancelText: L["Editor.Unsaved.Stay"]);
        if (leave != true)
        {
            context.PreventNavigation();
        }
    }
}
