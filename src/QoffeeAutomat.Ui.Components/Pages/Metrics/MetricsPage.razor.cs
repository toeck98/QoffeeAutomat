using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Metrics;

/// <summary>
/// The metric catalog (PLAN.md T405): the metrics by provider with the current value, the switches for <c>enabled</c> and
/// <c>exposeToHa</c> that save at once and spring back when the agent refuses, the interval and the deadband, and a hint on
/// the metrics that reveal what the user does. The agent keeps one override per metric and replaces it as a whole, so a
/// change sends every member of the override that should stay in effect.
/// </summary>
public partial class MetricsPage
{
    private readonly List<MetricInfo> _metrics = [];
    private readonly Dictionary<string, MetricOverride> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fieldErrors = new(StringComparer.Ordinal);
    private LiveMetricValues? _live;
    private ApiException? _listError;
    private ApiException? _actionError;
    private string _actionFailure = string.Empty;
    private bool _loading = true;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private DashboardOptions Options { get; set; } = null!;

    [Inject]
    private TimeProvider Time { get; set; } = null!;

    private IReadOnlyList<MetricGroup> Groups => MetricCatalogView.Group(_metrics);

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
        // The stream is closed first: the agent stops sampling when the last subscriber has gone.
        if (_live is not null)
        {
            await _live.DisposeAsync();
        }
    }

    private static bool IsNumeric(MetricInfo metric) => string.Equals(metric.ValueType, "number", StringComparison.Ordinal);

    private static string Aria(string action, MetricInfo metric) => action + ": " + metric.Name;

    private string GroupTitle(string key) => L.Find("Metrics.Group." + key) ?? key;

    private bool IsBusy(MetricInfo metric) => _busy.Contains(metric.Id);

    private bool IsEnabled(MetricInfo metric)
    {
        return _pending.TryGetValue(metric.Id, out MetricOverride? change) && change.Enabled is { } value ? value : metric.Enabled;
    }

    private bool IsExposed(MetricInfo metric)
    {
        return _pending.TryGetValue(metric.Id, out MetricOverride? change) && change.ExposeToHa is { } value ? value : metric.ExposeToHa;
    }

    private double IntervalOf(MetricInfo metric)
    {
        return _pending.TryGetValue(metric.Id, out MetricOverride? change) && change.IntervalSeconds is { } value ? value : metric.IntervalSeconds;
    }

    private double? DeadbandOf(MetricInfo metric)
    {
        return _pending.TryGetValue(metric.Id, out MetricOverride? change) && change.Deadband is { } value ? value : metric.Deadband;
    }

    /// <summary>The live value, else the one that the catalog came with.</summary>
    private JsonElement? ValueOf(MetricInfo metric)
    {
        if (_live is not null && _live.Values.TryGetValue(metric.Id, out JsonElement live))
        {
            return live;
        }

        return metric.HasValue ? metric.Value : null;
    }

    private int VersionOf(string id) => _versions.GetValueOrDefault(id);

    /// <summary>
    /// A control keeps what the user gave it even when the parent keeps passing the old value. A new key after every save makes
    /// the control start again from the saved state, which is how a refused change springs back.
    /// </summary>
    private string KeyOf(string field, MetricInfo metric) => field + "-" + metric.Id + "-" + VersionOf(metric.Id).ToString(CultureInfo.InvariantCulture);

    private string EnabledKey(MetricInfo metric) => KeyOf("enabled", metric);

    private string ExposeKey(MetricInfo metric) => KeyOf("expose", metric);

    private string IntervalKey(MetricInfo metric) => KeyOf("interval", metric);

    private string DeadbandKey(MetricInfo metric) => KeyOf("deadband", metric);

    private bool HasFieldError(MetricInfo metric, string field) => _fieldErrors.Contains(metric.Id + ":" + field);

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
            MetricList list = await Api.GetMetricsAsync();
            _metrics.Clear();
            _metrics.AddRange(list.Metrics);
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

        await UpdateStreamAsync();
    }

    /// <summary>The agent samples the metrics that are switched on while this page shows their values.</summary>
    private async Task UpdateStreamAsync()
    {
        if (_live is null || IsDisposed)
        {
            return;
        }

        await _live.SetMetricsAsync([.. _metrics.Where(metric => metric.Enabled).Select(metric => metric.Id)]);
    }

    private Task SetIntervalAsync(MetricInfo metric, string? text)
    {
        if (!MetricCatalogView.TryParseNonNegative(text, out double seconds))
        {
            return RejectFieldAsync(metric, "interval");
        }

        _fieldErrors.Remove(metric.Id + ":interval");
        return SaveAsync(metric, new MetricOverride(IntervalSeconds: seconds));
    }

    private Task SetDeadbandAsync(MetricInfo metric, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // An empty field means no deadband; the override cannot drop a single member, so zero says the same.
            _fieldErrors.Remove(metric.Id + ":deadband");
            return (metric.Deadband ?? 0) == 0 ? Task.CompletedTask : SaveAsync(metric, new MetricOverride(Deadband: 0));
        }

        if (!MetricCatalogView.TryParseNonNegative(text, out double deadband))
        {
            return RejectFieldAsync(metric, "deadband");
        }

        _fieldErrors.Remove(metric.Id + ":deadband");
        return SaveAsync(metric, new MetricOverride(Deadband: deadband));
    }

    private Task RejectFieldAsync(MetricInfo metric, string field)
    {
        // Nothing is sent; a new key puts the saved value back into the field next to the message.
        _fieldErrors.Add(metric.Id + ":" + field);
        _versions[metric.Id] = VersionOf(metric.Id) + 1;
        return Task.CompletedTask;
    }

    /// <summary>Saves one change at once. The control shows the new value while the agent answers and the answer decides.</summary>
    private async Task SaveAsync(MetricInfo metric, MetricOverride change)
    {
        if (!_busy.Add(metric.Id))
        {
            return;
        }

        _actionError = null;
        _fieldErrors.Remove(metric.Id + ":interval");
        _fieldErrors.Remove(metric.Id + ":deadband");

        // The override is replaced as a whole: the members that did not change stay as they are in effect.
        var whole = new MetricOverride(
            change.Enabled ?? metric.Enabled,
            change.ExposeToHa ?? metric.ExposeToHa,
            change.IntervalSeconds ?? metric.IntervalSeconds,
            change.Deadband ?? metric.Deadband);
        _pending[metric.Id] = whole;
        try
        {
            await Api.PutMetricOverrideAsync(metric.Id, whole);
            int index = _metrics.FindIndex(existing => string.Equals(existing.Id, metric.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _metrics[index] = _metrics[index] with
                {
                    Enabled = whole.Enabled ?? _metrics[index].Enabled,
                    ExposeToHa = whole.ExposeToHa ?? _metrics[index].ExposeToHa,
                    IntervalSeconds = whole.IntervalSeconds ?? _metrics[index].IntervalSeconds,
                    Deadband = whole.Deadband ?? _metrics[index].Deadband,
                };
            }

            await UpdateStreamAsync();
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            // Nothing was saved: the controls go back to the saved state below, and the error says why.
            _actionError = exception;
            _actionFailure = L.Format("Metrics.SaveFailed", metric.Name);
        }
        finally
        {
            _pending.Remove(metric.Id);
            _busy.Remove(metric.Id);
            _versions[metric.Id] = VersionOf(metric.Id) + 1;
        }
    }
}
