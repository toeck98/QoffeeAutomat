using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Pages.Metrics;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Sensors;

/// <summary>
/// The custom sensor list (PLAN.md T405): every sensor with its type and its live value, and the way into the editor. A sensor
/// whose type this platform cannot sample (a Windows sensor on another platform) is listed, so that it can be deleted, but not edited.
/// </summary>
public partial class SensorsPage
{
    private readonly List<SensorDocument> _sensors = [];
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private LiveMetricValues? _live;
    private string? _platform;
    private ApiException? _listError;
    private ApiException? _actionError;
    private string _actionFailure = string.Empty;
    private string? _notice;
    private bool _loading = true;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private IDialogService Dialogs { get; set; } = null!;

    [Inject]
    private DashboardOptions Options { get; set; } = null!;

    [Inject]
    private TimeProvider Time { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        _live = new LiveMetricValues(Api, Time, Options, () => InvokeAsync(StateHasChanged), () => InvokeAsync(Session.MarkSignedOut));
        await LoadAsync();
        _loading = false;
    }

    /// <inheritdoc />
    protected override async ValueTask OnDisposeAsync()
    {
        if (_live is not null)
        {
            await _live.DisposeAsync();
        }
    }

    private static string EditHref(SensorDocument sensor) => "/sensors/" + Uri.EscapeDataString(sensor.Id);

    private static string Aria(string action, SensorDocument sensor) => action + ": " + sensor.Name;

    private string TypeText(string type) => L.Find("Sensors.Type." + type) ?? type;

    private bool IsOffered(SensorDocument sensor) => SensorAttributeRules.IsOffered(_platform, sensor.Source.Type);

    private bool IsBusy(SensorDocument sensor) => _busy.Contains(sensor.Id);

    private JsonElement? ValueOf(SensorDocument sensor)
    {
        return _live is not null && _live.Values.TryGetValue(SensorFormat.MetricId(sensor.Id), out JsonElement value) ? value : null;
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
            SensorList list = await Api.GetSensorsAsync();
            _sensors.Clear();
            _sensors.AddRange(list.Sensors.OrderBy(sensor => sensor.Id, StringComparer.Ordinal));
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
            // Without the platform only the types that every platform has are editable.
            _platform = null;
        }

        await UpdateStreamAsync();
    }

    /// <summary>The agent samples the sensors that are switched on while this page shows their values.</summary>
    private async Task UpdateStreamAsync()
    {
        if (_live is null || IsDisposed)
        {
            return;
        }

        await _live.SetMetricsAsync([.. _sensors.Where(sensor => sensor.Enabled).Select(sensor => SensorFormat.MetricId(sensor.Id))]);
    }

    private async Task DeleteAsync(SensorDocument sensor)
    {
        bool? confirmed = await Dialogs.ShowMessageBox(
            L["Sensors.Delete.Title"],
            L.Format("Sensors.Delete.Message", sensor.Name, sensor.Id),
            yesText: L["Sensors.Delete"],
            cancelText: L["Common.Cancel"]);
        if (confirmed != true || IsDisposed || !_busy.Add(sensor.Id))
        {
            return;
        }

        _actionError = null;
        _notice = null;
        try
        {
            await Api.DeleteSensorAsync(sensor.Id);
            _sensors.RemoveAll(existing => string.Equals(existing.Id, sensor.Id, StringComparison.Ordinal));
            _notice = L.Format("Sensors.Deleted", sensor.Name);
            await UpdateStreamAsync();
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L.Format("Sensors.DeleteFailed", sensor.Name);
        }
        finally
        {
            _busy.Remove(sensor.Id);
        }
    }
}
