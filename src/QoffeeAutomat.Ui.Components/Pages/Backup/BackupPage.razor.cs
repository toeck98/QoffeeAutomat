using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Backup;

/// <summary>
/// The backup page (PLAN.md T407). Export and diagnostics hand a file to the browser. An import is read in the browser, sent to the
/// agent for a preview, and applied only when the preview says every document is valid; the agent checks the whole archive again
/// before it writes, so an invalid document applies nothing. The revision history lists the kept revisions of one document and
/// restores one of them as a new revision.
/// </summary>
public partial class BackupPage
{
    /// <summary>The largest archive that the agent accepts (32 MiB).</summary>
    private const long MaxArchiveBytes = 32L * 1024 * 1024;

    private const string SettingsId = "settings";

    private readonly List<string> _documentIds = [];

    private ApiException? _actionError;
    private string _actionFailure = string.Empty;
    private string? _notice;
    private bool _exporting;
    private bool _downloadingDiagnostics;

    private string? _fileName;
    private byte[]? _archive;
    private bool _includeSettings;
    private ConfigImportPreview? _preview;
    private ApiException? _previewError;
    private string? _fileError;
    private bool _previewing;
    private bool _importing;

    private string _kind = RevisionKinds.Command;
    private string _documentId = string.Empty;
    private RevisionList? _revisions;
    private ApiException? _revisionsError;
    private bool _loadingRevisions;
    private bool _restoring;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private IBrowserInterop Browser { get; set; } = null!;

    [Inject]
    private IDialogService Dialogs { get; set; } = null!;

    private bool CanApply => _preview is { Valid: true } preview && preview.Items.Any(item => item.Change != "unchanged");

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadDocumentIdsAsync();
    }

    private static Color ChangeColor(string change) => change switch
    {
        "new" => Color.Success,
        "changed" => Color.Warning,
        _ => Color.Default,
    };

    private int Count(string change) => _preview?.Items.Count(item => item.Change == change) ?? 0;

    private async Task ExportAsync()
    {
        if (_exporting)
        {
            return;
        }

        _exporting = true;
        ClearMessages();
        try
        {
            DownloadedFile file = await Api.ExportConfigAsync();
            await Browser.DownloadFileAsync(file.FileName, file.ContentType, file.Content);
            _notice = L.Format("Backup.Export.Done", file.FileName);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L["Backup.Export.Failed"];
        }
        finally
        {
            _exporting = false;
        }
    }

    private async Task DownloadDiagnosticsAsync()
    {
        if (_downloadingDiagnostics)
        {
            return;
        }

        _downloadingDiagnostics = true;
        ClearMessages();
        try
        {
            DownloadedFile file = await Api.DownloadDiagnosticsAsync();
            await Browser.DownloadFileAsync(file.FileName, file.ContentType, file.Content);
            _notice = L.Format("Backup.Diagnostics.Done", file.FileName);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L["Backup.Diagnostics.Failed"];
        }
        finally
        {
            _downloadingDiagnostics = false;
        }
    }

    private async Task OnFileChosenAsync(InputFileChangeEventArgs args)
    {
        ClearMessages();
        _preview = null;
        _previewError = null;
        _fileError = null;
        _archive = null;
        _fileName = null;

        IBrowserFile? file = args.FileCount > 0 ? args.File : null;
        if (file is null)
        {
            return;
        }

        if (file.Size > MaxArchiveBytes)
        {
            _fileError = L["Backup.Import.TooLarge"];
            return;
        }

        try
        {
            await using Stream stream = file.OpenReadStream(MaxArchiveBytes);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            _archive = buffer.ToArray();
            _fileName = file.Name;
        }
        catch (IOException)
        {
            _fileError = L["Backup.Import.Unreadable"];
            return;
        }

        await PreviewAsync();
    }

    private async Task SetIncludeSettingsAsync(bool include)
    {
        _includeSettings = include;
        if (_archive is not null)
        {
            await PreviewAsync();
        }
    }

    private async Task PreviewAsync()
    {
        if (_archive is null)
        {
            return;
        }

        _previewing = true;
        _previewError = null;
        _preview = null;
        try
        {
            _preview = await Api.PreviewImportAsync(_archive, _includeSettings);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _previewError = exception;
        }
        finally
        {
            _previewing = false;
        }
    }

    private async Task ApplyAsync()
    {
        if (_archive is null || !CanApply || _importing)
        {
            return;
        }

        _importing = true;
        ClearMessages();
        try
        {
            ConfigImportResult result = await Api.ImportConfigAsync(_archive, _includeSettings);
            _notice = L.Format("Backup.Import.Done", result.Commands, result.Sensors, result.Scripts);
            _archive = null;
            _fileName = null;
            _preview = null;
            await LoadDocumentIdsAsync();
            await LoadRevisionsAsync();
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception) when (exception.StatusCode == 422)
        {
            // The agent found a problem the preview did not: it applied nothing, and the errors are the preview now.
            _preview = new ConfigImportPreview(false, _preview?.Items ?? [], exception.ValidationErrors);
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L["Backup.Import.Failed"];
        }
        finally
        {
            _importing = false;
        }
    }

    private async Task SetKindAsync(string kind)
    {
        _kind = kind;
        _documentId = string.Empty;
        _revisions = null;
        _revisionsError = null;
        if (kind == RevisionKinds.Settings)
        {
            _documentId = SettingsId;
            await LoadRevisionsAsync();
        }
        else
        {
            await LoadDocumentIdsAsync();
        }
    }

    private async Task SetDocumentAsync(string id)
    {
        _documentId = id;
        await LoadRevisionsAsync();
    }

    /// <summary>Reads the ids of the commands or sensors that the revision picker offers.</summary>
    private async Task LoadDocumentIdsAsync()
    {
        _documentIds.Clear();
        if (_kind == RevisionKinds.Settings)
        {
            return;
        }

        try
        {
            if (_kind == RevisionKinds.Sensor)
            {
                SensorList sensors = await Api.GetSensorsAsync();
                _documentIds.AddRange(sensors.Sensors.Select(sensor => sensor.Id).Order(StringComparer.Ordinal));
            }
            else
            {
                CommandList commands = await Api.GetCommandsAsync();
                _documentIds.AddRange(commands.Commands.Select(command => command.Id).Order(StringComparer.Ordinal));
            }
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _revisionsError = exception;
        }
    }

    private async Task LoadRevisionsAsync()
    {
        if (_documentId.Length == 0)
        {
            _revisions = null;
            return;
        }

        _loadingRevisions = true;
        _revisionsError = null;
        try
        {
            _revisions = await Api.GetRevisionsAsync(_kind, _documentId);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _revisions = null;
            _revisionsError = exception;
        }
        finally
        {
            _loadingRevisions = false;
        }
    }

    private async Task RestoreAsync(RevisionEntry entry)
    {
        if (_revisions is null || _restoring)
        {
            return;
        }

        bool? confirmed = await Dialogs.ShowMessageBox(
            L["Backup.Revisions.Restore.Title"],
            L.Format("Backup.Revisions.Restore.Message", _documentId, entry.Revision),
            yesText: L["Backup.Revisions.Restore"],
            cancelText: L["Common.Cancel"]);
        if (confirmed != true || IsDisposed)
        {
            return;
        }

        _restoring = true;
        ClearMessages();
        try
        {
            await Api.RestoreRevisionAsync(_kind, _documentId, entry.Revision, _revisions.Current);
            _notice = L.Format("Backup.Revisions.Restored", _documentId, entry.Revision);
            await LoadRevisionsAsync();
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L["Backup.Revisions.RestoreFailed"];
            if (exception.StatusCode == 412)
            {
                // Somebody saved the document meanwhile: show its current revisions, so the next try is against them.
                await LoadRevisionsAsync();
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void ClearMessages()
    {
        _actionError = null;
        _notice = null;
    }
}
