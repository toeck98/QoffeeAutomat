using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Templates;

/// <summary>
/// The template gallery (PLAN.md T701): the templates that ship with the agent, a preview of one with its scripts, and adding
/// one as a new command or sensor. The agent makes the id unique and copies the scripts, so adding the same template twice
/// gives two independent copies.
/// </summary>
public partial class TemplatesPage
{
    private readonly List<TemplateSummary> _templates = [];
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private TemplateDetail? _preview;
    private ApiException? _listError;
    private ApiException? _previewError;
    private ApiException? _actionError;
    private string _actionFailure = string.Empty;
    private TemplateInstantiation? _added;
    private string? _addedName;
    private string? _addedHref;
    private string _addedScripts = string.Empty;
    private string _search = string.Empty;
    private bool _loading = true;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _loading = false;
    }

    private bool IsBusy(string id) => _busy.Contains(id);

    private string TypeText(string? type) => type is null ? string.Empty : L.Find("Templates.Type." + type) ?? type;

    /// <summary>The text of the button that opens what a template created.</summary>
    private string OpenText(string kind) => kind switch
    {
        TemplateKinds.Command => L["Templates.OpenCommand"],
        TemplateKinds.Mode => L["Templates.OpenMode"],
        _ => L["Templates.OpenSensor"],
    };

    /// <summary>The templates that pass the search, in the two groups of the page; a group without a match is left out.</summary>
    private List<TemplateGroup> Groups()
    {
        TemplateSummary[] shown = [.. _templates.Where(template => TemplateFilter.Matches(template, _search))];
        List<TemplateGroup> groups = [];
        AddGroup(groups, L["Templates.Commands"], "templates-commands", shown, TemplateKinds.Command);
        AddGroup(groups, L["Templates.Sensors"], "templates-sensors", shown, TemplateKinds.Sensor);
        AddGroup(groups, L["Templates.Modes"], "templates-modes", shown, TemplateKinds.Mode);
        return groups;
    }

    private static void AddGroup(List<TemplateGroup> groups, string title, string testId, TemplateSummary[] shown, string kind)
    {
        TemplateSummary[] items = [.. shown.Where(template => template.Kind == kind)];
        if (items.Length > 0)
        {
            groups.Add(new TemplateGroup(title, testId, items));
        }
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
            TemplateList list = await Api.GetTemplatesAsync();
            _templates.Clear();
            _templates.AddRange(list.Templates);
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

    private async Task PreviewAsync(TemplateSummary template)
    {
        if (!_busy.Add(template.Id))
        {
            return;
        }

        _previewError = null;
        _actionError = null;
        try
        {
            _preview = await Api.GetTemplateAsync(template.Id);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _preview = null;
            _previewError = exception;
        }
        finally
        {
            _busy.Remove(template.Id);
        }
    }

    private void ClosePreview()
    {
        _preview = null;
        _previewError = null;
    }

    private async Task AddAsync(TemplateSummary template)
    {
        if (!_busy.Add(template.Id))
        {
            return;
        }

        _actionError = null;
        _added = null;
        try
        {
            TemplateInstantiation created = await Api.InstantiateTemplateAsync(template.Id);
            _added = created;
            _addedName = template.Name;
            _addedHref = TemplateFilter.EditorHref(created.Kind, created.Id);
            _addedScripts = string.Join(", ", created.Created.Where(item => item.Kind == TemplateKinds.Script).Select(item => item.Id));
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L.Format("Templates.AddFailed", template.Name);
        }
        finally
        {
            _busy.Remove(template.Id);
        }
    }

    private sealed record TemplateGroup(string Title, string TestId, IReadOnlyList<TemplateSummary> Items);
}
