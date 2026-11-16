using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.CommandEditor;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Sensors;

/// <summary>
/// The sensor editor (PLAN.md T405). It owns the draft, tests it against the real source without saving (the agent samples the
/// draft once and answers with the parsed value or the error), saves it, and shows the problems that the agent reports at the
/// fields they belong to. Saving a new sensor stays in the editor: the address changes to the one of the saved sensor.
/// </summary>
public partial class SensorEditorPage
{
    /// <summary>The id that a test uses while the id of the draft is not a valid one yet (the id plays no part in sampling).</summary>
    private const string TestId = "test_sensor";

    private SensorDraft _draft = new();
    private DraftErrors _errors = DraftErrors.Empty;
    private IReadOnlyList<string> _scripts = [];
    private string? _platform;
    private string? _loadedId;
    private string? _notice;
    private ApiException? _loadError;
    private ApiException? _saveError;
    private ApiException? _testError;
    private SensorTestResult? _testResult;
    private SensorDocument? _tested;
    private bool _initialised;
    private bool _idTouched;
    private bool _loading = true;
    private bool _notFound;
    private bool _saving;
    private bool _testing;

    /// <summary>The id of the sensor to change; <c>null</c> on the page of a new sensor.</summary>
    [Parameter]
    public string? Id { get; set; }

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    private string TitleText => _draft.IsNew ? L["Sensors.Title.New"] : L.Format("Sensors.Title.Edit", _draft.Name);

    private string TestState => _testing ? "running" : _testError is not null ? "error" : _testResult is null ? "none" : _testResult.Succeeded ? "succeeded" : "failed";

    /// <summary>The source types that this platform can sample, and the type of the sensor when it is none of them.</summary>
    private IReadOnlyList<string> SourceTypeChoices => With(SensorAttributeRules.OfferedSourceTypes(_platform), _draft.SourceType);

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (!_initialised)
        {
            _initialised = true;
            await LoadLookupsAsync();
        }

        if (Id is null)
        {
            if (_loadedId is not null)
            {
                _draft = new SensorDraft();
                _loadedId = null;
                ResetResults();
            }

            _loading = false;
            return;
        }

        if (string.Equals(Id, _loadedId, StringComparison.Ordinal))
        {
            return;
        }

        await LoadSensorAsync(Id);
    }

    private static IReadOnlyList<string> With(IReadOnlyList<string> choices, string current)
    {
        return string.IsNullOrEmpty(current) || choices.Contains(current, StringComparer.Ordinal) ? choices : [.. choices, current];
    }

    private string TextOf(ApiValidationError error)
    {
        return L.Find("Editor.Error." + error.Code) ?? error.Message;
    }

    private async Task ReloadAsync()
    {
        _loadedId = null;
        await LoadSensorAsync(Id);
    }

    /// <summary>The platform decides which source types are offered; the scripts are the choices of a script-file source.</summary>
    private async Task LoadLookupsAsync()
    {
        try
        {
            _platform = (await Api.GetStatusAsync()).Platform.Kind;
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
            return;
        }
        catch (ApiException)
        {
            // Without the platform only the types that every platform has are offered.
            _platform = null;
        }

        try
        {
            _scripts = [.. (await Api.GetScriptsAsync()).Scripts.Select(script => script.Name)];
        }
        catch (ApiException)
        {
            // The scripts are a convenience; an inline script works without them.
        }
    }

    private async Task LoadSensorAsync(string? id)
    {
        _loading = true;
        _loadError = null;
        _notFound = false;
        ResetResults();
        try
        {
            if (id is null)
            {
                _draft = new SensorDraft();
                _loadedId = null;
                return;
            }

            SensorList list = await Api.GetSensorsAsync();
            SensorDocument? sensor = list.Sensors.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (sensor is null)
            {
                _notFound = true;
                return;
            }

            _draft = SensorDraft.From(sensor);
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
        _testError = null;
        _testResult = null;
        _tested = null;
        _notice = null;
        _idTouched = false;
    }

    private void OnNameChanged()
    {
        // A new sensor gets an id from its name until the user types one.
        if (_draft.IsNew && !_idTouched)
        {
            _draft.Id = CommandSlug.FromName(_draft.Name);
        }
    }

    private void OnIdChanged()
    {
        _idTouched = true;
    }

    private void OnValueTypeChanged(string valueType)
    {
        _draft.ValueType = valueType;

        // A class that does not fit the new value type would be refused by Home Assistant.
        if (!SensorAttributeRules.DeviceClassesFor(valueType).Contains(_draft.DeviceClass, StringComparer.Ordinal))
        {
            _draft.DeviceClass = string.Empty;
        }

        if (!SensorAttributeRules.AllowsUnit(valueType))
        {
            _draft.Unit = string.Empty;
        }

        if (!SensorAttributeRules.AllowsStateClass(valueType))
        {
            _draft.StateClass = string.Empty;
        }
    }

    private async Task TestAsync()
    {
        if (_testing)
        {
            return;
        }

        _testError = null;
        _testResult = null;
        SensorBuild build = _draft.Build();
        if (build.Problems.Count > 0)
        {
            _errors = DraftErrors.From(build.Problems, []);
            return;
        }

        // A draft without a usable id or name can still be sampled: neither plays a part in it.
        SensorDocument draft = build.Document with
        {
            Id = CommandSlug.IsValid(build.Document.Id) ? build.Document.Id : TestId,
            Name = string.IsNullOrWhiteSpace(build.Document.Name) ? TestId : build.Document.Name,
        };
        _errors = DraftErrors.Empty;
        _testing = true;
        try
        {
            SensorTestResult result = await Api.TestSensorAsync(draft);
            if (IsDisposed)
            {
                return;
            }

            _tested = draft;
            _testResult = result;
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception) when (exception.StatusCode == 422 && exception.ValidationErrors.Count > 0)
        {
            // The draft is not valid (or not supported here): the fields say why.
            _errors = DraftErrors.From([], exception.ValidationErrors);
        }
        catch (ApiException exception)
        {
            _testError = exception;
        }
        finally
        {
            _testing = false;
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
        SensorBuild build = _draft.Build();
        if (build.Problems.Count > 0)
        {
            // A value that is no value: the fields say which; nothing is sent.
            _errors = DraftErrors.From(build.Problems, []);
            return;
        }

        _errors = DraftErrors.Empty;
        _saving = true;
        bool wasNew = _draft.IsNew;
        try
        {
            SensorDocument saved = wasNew
                ? await Api.CreateSensorAsync(build.Document)
                : await Api.ReplaceSensorAsync(build.Document);
            if (IsDisposed)
            {
                return;
            }

            _draft.Adopt(saved);
            _notice = L.Format("Sensors.Saved", saved.Name);
            if (wasNew)
            {
                // The editor stays; only the address becomes the one of the sensor, so that a reload finds it.
                _loadedId = saved.Id;
                Navigation.NavigateTo("/sensors/" + Uri.EscapeDataString(saved.Id), replace: true);
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
            _errors = DraftErrors.From([], [new ApiValidationError("/id", "already_exists", L["Sensors.Error.already_exists"])]);
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
