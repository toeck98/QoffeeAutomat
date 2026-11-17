using Microsoft.AspNetCore.Components;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Secrets;

/// <summary>
/// The named secrets of scripts (PLAN.md T706). The page lists the names and the commands that use them, adds a secret or
/// replaces the value of one, and deletes one after a confirmation. A value goes to the agent and is forgotten at once: it is
/// never kept in a list, never shown and never read back, because the agent has no call that returns it.
/// </summary>
public partial class SecretsPage
{
    private readonly List<SecretSummary> _secrets = [];
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private ApiException? _listError;
    private ApiException? _saveError;
    private ApiException? _actionError;
    private string _saveFailure = string.Empty;
    private string _actionFailure = string.Empty;
    private string? _notice;
    private string _name = string.Empty;
    private string _value = string.Empty;
    private bool _loading = true;
    private bool _saving;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private IDialogService Dialogs { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _loading = false;
    }

    private static string VariableOf(string name) => "QA_SECRET_" + name.ToUpperInvariant();

    private bool IsBusy(string name) => _busy.Contains(name);

    private string UsedByText(SecretSummary secret)
    {
        return secret.UsedBy is { Count: > 0 } users ? string.Join(", ", users) : L["Secrets.UsedBy.None"];
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
            SecretList list = await Api.GetSecretsAsync();
            _secrets.Clear();
            _secrets.AddRange(list.Secrets);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _listError = exception;
        }
    }

    /// <summary>Fills in the name of an existing secret, so that only the new value has to be typed.</summary>
    private void Replace(SecretSummary secret)
    {
        _name = secret.Name;
        _value = string.Empty;
        _saveError = null;
        _notice = null;
    }

    private async Task SaveAsync()
    {
        string name = _name.Trim();
        if (_saving || name.Length == 0 || _value.Length == 0)
        {
            return;
        }

        _saving = true;
        _saveError = null;
        _actionError = null;
        _notice = null;
        try
        {
            bool existed = _secrets.Exists(secret => string.Equals(secret.Name, name, StringComparison.Ordinal));
            await Api.PutSecretAsync(name, _value);

            // The value is not needed any more; the field is emptied before anything else can render it.
            _value = string.Empty;
            _name = string.Empty;
            _notice = existed ? L.Format("Secrets.Replaced", name) : L.Format("Secrets.Created", name);
            await LoadAsync();
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _saveError = exception;
            _saveFailure = L.Format("Secrets.SaveFailed", name);
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task DeleteAsync(SecretSummary secret)
    {
        string message = secret.UsedBy is { Count: > 0 } users
            ? L.Format("Secrets.Delete.MessageUsed", secret.Name, string.Join(", ", users))
            : L.Format("Secrets.Delete.Message", secret.Name);
        bool? confirmed = await Dialogs.ShowMessageBox(
            L["Secrets.Delete.Title"],
            message,
            yesText: L["Secrets.Delete"],
            cancelText: L["Common.Cancel"]);
        if (confirmed != true || IsDisposed || !_busy.Add(secret.Name))
        {
            return;
        }

        _actionError = null;
        _notice = null;
        try
        {
            await Api.DeleteSecretAsync(secret.Name);
            _secrets.RemoveAll(existing => string.Equals(existing.Name, secret.Name, StringComparison.Ordinal));
            _notice = L.Format("Secrets.Deleted", secret.Name);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L.Format("Secrets.DeleteFailed", secret.Name);
        }
        finally
        {
            _busy.Remove(secret.Name);
        }
    }
}
