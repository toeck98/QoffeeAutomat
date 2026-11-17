using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.CommandEditor;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Modes;

/// <summary>
/// The mode group editor (PLAN.md T717). It owns the draft, saves it, and shows the problems that the editor finds itself and
/// the ones that the agent reports at the fields they belong to. The enter and exit steps of every mode are edited with the
/// step editor of the sequence action. Saving a new group stays in the editor: the address changes to the one of the saved group.
/// </summary>
public partial class ModeGroupEditorPage
{
    /// <summary>The problem code for a group id that is taken already.</summary>
    private const string GroupExistsCode = "group_exists";

    private static readonly string[] BootBehaviors = [ModeBootBehaviors.Reset, ModeBootBehaviors.Keep, ModeBootBehaviors.ApplyDefault];

    private static readonly string[] ErrorBehaviors = [SequenceErrorModes.Stop, SequenceErrorModes.Continue];

    private readonly List<string> _commandIds = [];
    private ModeGroupDraft _draft = new();
    private DraftErrors _errors = DraftErrors.Empty;
    private string? _loadedId;
    private string? _notice;
    private ApiException? _loadError;
    private ApiException? _saveError;
    private bool _initialised;
    private bool _idTouched;
    private bool _loading = true;
    private bool _notFound;
    private bool _saving;

    /// <summary>The id of the group to change; <c>null</c> on the page of a new group.</summary>
    [Parameter]
    public string? Id { get; set; }

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    private string TitleText => _draft.IsNew ? L["Modes.Title.New"] : L.Format("Modes.Title.Edit", _draft.Name);

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (!_initialised)
        {
            _initialised = true;
            await LoadCommandsAsync();
        }

        if (Id is null)
        {
            if (_loadedId is not null || _notFound)
            {
                _draft = new ModeGroupDraft();
                _loadedId = null;
                ResetResults();
            }

            _notFound = false;
            _loadError = null;
            _loading = false;
            return;
        }

        if (string.Equals(Id, _loadedId, StringComparison.Ordinal))
        {
            return;
        }

        await LoadGroupAsync(Id);
    }

    private static IReadOnlyList<string> With(IReadOnlyList<string> choices, string current)
    {
        return string.IsNullOrEmpty(current) || choices.Contains(current, StringComparer.Ordinal) ? choices : [.. choices, current];
    }

    private static string ModeLabel(ModeDraft mode, int index)
    {
        return string.IsNullOrWhiteSpace(mode.Label) ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : mode.Label;
    }

    private string TextOf(ApiValidationError error)
    {
        return L.Find("Editor.Error." + error.Code) ?? error.Message;
    }

    private async Task ReloadAsync()
    {
        _loadedId = null;
        if (Id is null)
        {
            _loadError = null;
            _loading = false;
            return;
        }

        await LoadGroupAsync(Id);
    }

    /// <summary>The commands that a step can run. Failing to read them only leaves the choice to what the steps already name.</summary>
    private async Task LoadCommandsAsync()
    {
        try
        {
            CommandList list = await Api.GetCommandsAsync();
            _commandIds.Clear();
            _commandIds.AddRange(list.Commands.Select(command => command.Id).OrderBy(id => id, StringComparer.Ordinal));
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException)
        {
            // A step can still keep the command that it names.
        }
    }

    private async Task LoadGroupAsync(string id)
    {
        _loading = true;
        _loadError = null;
        _notFound = false;
        ResetResults();
        try
        {
            ModeGroupList list = await Api.GetModeGroupsAsync();
            ModeGroupDocument? group = list.Groups
                .Select(item => item.Group)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (group is null)
            {
                _notFound = true;
                return;
            }

            _draft = ModeGroupDraft.From(group);
            _loadedId = id;
            _idTouched = true;
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _loadError = exception;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ResetResults()
    {
        _errors = DraftErrors.Empty;
        _saveError = null;
        _notice = null;
        _idTouched = false;
    }

    private void OnNameChanged()
    {
        // A new group gets an id from its name until the user types one.
        if (_draft.IsNew && !_idTouched)
        {
            _draft.Id = CommandSlug.FromName(_draft.Name);
        }
    }

    private void OnIdChanged()
    {
        _idTouched = true;
    }

    private static void OnModeNameChanged(ModeDraft mode)
    {
        // A new mode gets an id from its name until the user types one.
        if (!mode.IdTouched)
        {
            mode.Id = CommandSlug.FromName(mode.Name);
        }
    }

    private static void OnModeIdChanged(ModeDraft mode)
    {
        mode.IdTouched = true;
    }

    private void AddMode()
    {
        _draft.AddMode();
    }

    private void RemoveMode(int index)
    {
        _draft.RemoveMode(index);
        _errors = DraftErrors.Empty;
    }

    private void MoveMode(int index, int offset)
    {
        _draft.MoveMode(index, offset);
        _errors = DraftErrors.Empty;
    }

    private async Task SaveAsync()
    {
        if (_saving)
        {
            return;
        }

        _saveError = null;
        _notice = null;
        ModeGroupBuild build = _draft.Build();
        if (build.Problems.Count > 0)
        {
            // What the editor sees by itself: the fields say which; nothing is sent.
            _errors = DraftErrors.From(build.Problems, []);
            return;
        }

        _errors = DraftErrors.Empty;
        _saving = true;
        bool wasNew = _draft.IsNew;
        try
        {
            ModeGroupDocument saved = wasNew
                ? await Api.CreateModeGroupAsync(build.Document)
                : await Api.ReplaceModeGroupAsync(build.Document);
            if (IsDisposed)
            {
                return;
            }

            _draft.Adopt(saved);
            _notice = L.Format("Modes.Saved", saved.Name);
            if (wasNew)
            {
                // The editor stays; only the address becomes the one of the group, so that a reload finds it.
                _loadedId = saved.Id;
                Navigation.NavigateTo("/modes/" + Uri.EscapeDataString(saved.Id), replace: true);
            }
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception) when (exception.StatusCode == 422 && exception.ValidationErrors.Count > 0)
        {
            _errors = DraftErrors.From([], exception.ValidationErrors);
        }
        catch (ApiException exception) when (wasNew && exception.StatusCode == 409)
        {
            _errors = DraftErrors.From([], [new ApiValidationError("/id", GroupExistsCode, L["Modes.Error.group_exists"])]);
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
}
