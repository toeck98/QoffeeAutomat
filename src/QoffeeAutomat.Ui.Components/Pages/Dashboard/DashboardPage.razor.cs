using Microsoft.AspNetCore.Components;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Dashboard;

/// <summary>
/// The dashboard (PLAN.md T402): agent status, Home Assistant link, metric tiles with live values and charts, and the
/// latest runs and events. The live metric stream and the periodic refresh exist only while the page is visible: they are
/// opened when the page appears in the foreground, closed when the tab is hidden and closed for good when the user
/// leaves the page, so the agent samples metrics for nobody in the background (PLAN.md D-007).
/// </summary>
public partial class DashboardPage
{
    private const int MaxPointsPerTile = 600;

    private readonly List<MetricTileModel> _tiles = [];
    private AgentStatus? _status;
    private LinkClientInfo[] _clients = [];
    private RunSummary[] _runs = [];
    private EventInfo[] _events = [];
    private ApiException? _error;
    private bool _loading = true;
    private bool _unauthorized;
    private bool _visible;
    private bool _visibilityKnown;
    private LiveState _liveState;
    private LiveFeed? _stream;
    private LiveFeed? _refresh;
    private IAsyncDisposable? _visibilitySubscription;

    private enum LiveState
    {
        Paused = 0,
        Connecting,
        Live,
        Reconnecting,
    }

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private IBrowserInterop Browser { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private DashboardOptions Options { get; set; } = null!;

    [Inject]
    private TimeProvider Time { get; set; } = null!;

    private int HistoryMinutes => Math.Max(1, Options.HistorySeconds / 60);

    private string LiveStateName => _liveState.ToString().ToLowerInvariant();

    private Color LiveColor => _liveState switch
    {
        LiveState.Live => Color.Success,
        LiveState.Paused => Color.Default,
        _ => Color.Warning,
    };

    private string LiveText => _liveState switch
    {
        LiveState.Live => L["Dashboard.Metrics.Live"],
        LiveState.Connecting => L["Dashboard.Metrics.Connecting"],
        LiveState.Reconnecting => L["Dashboard.Metrics.Reconnecting"],
        _ => L["Dashboard.Metrics.Paused"],
    };

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _loading = false;
        if (IsDisposed || _unauthorized)
        {
            return;
        }

        // Listen first, then ask, so that a change in between is not lost.
        IAsyncDisposable subscription = await Browser.SubscribePageVisibilityAsync(OnVisibilityChangedAsync);
        if (IsDisposed)
        {
            await subscription.DisposeAsync();
            return;
        }

        _visibilitySubscription = subscription;
        bool visible = await Browser.IsPageVisibleAsync();
        if (IsDisposed)
        {
            return;
        }

        if (!_visibilityKnown)
        {
            _visible = visible;
        }

        if (_visible)
        {
            StartLive();
        }
    }

    /// <inheritdoc />
    protected override async ValueTask OnDisposeAsync()
    {
        // The stream is closed before anything else: the agent stops sampling as soon as the last subscriber has gone.
        await StopLiveAsync();
        IAsyncDisposable? subscription = _visibilitySubscription;
        _visibilitySubscription = null;
        if (subscription is not null)
        {
            await subscription.DisposeAsync();
        }
    }

    private async Task ReloadAsync()
    {
        await StopLiveAsync();
        _loading = true;
        await LoadAsync();
        _loading = false;
        if (_visible && !IsDisposed && !_unauthorized)
        {
            StartLive();
        }
    }

    private async Task OnVisibilityChangedAsync(bool visible)
    {
        await InvokeAsync(async () =>
        {
            _visibilityKnown = true;
            _visible = visible;
            if (IsDisposed)
            {
                return;
            }

            if (visible)
            {
                // Values may be old after a while in the background: read them again, then go live.
                await RefreshAsync(CancellationToken.None);
                if (_visible && !IsDisposed)
                {
                    StartLive();
                }
            }
            else
            {
                await StopLiveAsync();
            }

            StateHasChanged();
        });
    }

    private void StartLive()
    {
        if (IsDisposed || _refresh is not null)
        {
            return;
        }

        if (_tiles.Count > 0)
        {
            _liveState = LiveState.Connecting;
            _stream = new LiveFeed(RunStreamAsync);
        }

        _refresh = new LiveFeed(RunRefreshAsync);
        StateHasChanged();
    }

    private async Task StopLiveAsync()
    {
        LiveFeed? stream = _stream;
        LiveFeed? refresh = _refresh;
        _stream = null;
        _refresh = null;
        _liveState = LiveState.Paused;
        if (stream is not null)
        {
            await stream.DisposeAsync();
        }

        if (refresh is not null)
        {
            await refresh.DisposeAsync();
        }
    }

    private async Task RunStreamAsync(CancellationToken cancellationToken)
    {
        string[] ids = [.. _tiles.Select(tile => tile.Info.Id)];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (MetricStreamUpdate update in Api.StreamMetricsAsync(ids, Options.StreamIntervalSeconds, cancellationToken))
                {
                    ApplyUpdate(update);
                    _liveState = LiveState.Live;
                    await InvokeAsync(StateHasChanged);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // The connection timed out; it is opened again below.
            }
            catch (ApiException exception) when (exception.IsUnauthorized)
            {
                Session.MarkSignedOut();
                break;
            }
            catch (ApiException)
            {
                // The agent is not reachable or refused; it is tried again below.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _liveState = LiveState.Reconnecting;
            await InvokeAsync(StateHasChanged);
            try
            {
                await Task.Delay(Options.ReconnectDelay, Time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunRefreshAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Options.RefreshInterval, Time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left or hidden.
        }
    }

    private void ApplyUpdate(MetricStreamUpdate update)
    {
        TimeSpan window = TimeSpan.FromSeconds(Options.HistorySeconds);
        foreach (MetricTileModel tile in _tiles)
        {
            if (update.Values.TryGetValue(tile.Info.Id, out System.Text.Json.JsonElement value))
            {
                tile.Apply(update.Timestamp, value, window, MaxPointsPerTile);
            }
        }
    }

    private async Task LoadAsync()
    {
        _error = null;
        _unauthorized = false;

        _status = await CallAsync(() => Api.GetStatusAsync()) ?? _status;
        if (await CallAsync(() => Api.GetLinkClientsAsync()) is { } clients)
        {
            _clients = [.. clients.Clients];
        }

        if (await CallAsync(() => Api.GetMetricsAsync()) is { } catalog)
        {
            await LoadTilesAsync(catalog);
        }

        await LoadRunsAndEventsAsync(CancellationToken.None);
    }

    private async Task LoadTilesAsync(MetricList catalog)
    {
        _tiles.Clear();
        _tiles.AddRange(MetricSelector.Pick(catalog.Metrics, Options.MaxTiles).Select(metric => new MetricTileModel(metric)));
        if (_tiles.Count == 0)
        {
            return;
        }

        string[] ids = [.. _tiles.Select(tile => tile.Info.Id)];
        MetricHistoryResponse? history = await CallAsync(() => Api.GetMetricHistoryAsync(ids, Options.HistorySeconds));
        if (history is null)
        {
            return;
        }

        foreach (MetricSeries series in history.Series)
        {
            _tiles.Find(tile => string.Equals(tile.Info.Id, series.Id, StringComparison.Ordinal))?.SetHistory(series.Points);
        }
    }

    private async Task LoadRunsAndEventsAsync(CancellationToken cancellationToken)
    {
        if (await CallAsync(() => Api.GetRunsAsync(Options.RecentRuns, cancellationToken)) is { } runs)
        {
            _runs = [.. runs.Runs];
        }

        if (await CallAsync(() => Api.GetEventsAsync(Options.RecentEvents, cancellationToken)) is { } events)
        {
            _events = [.. events.Events];
        }
    }

    /// <summary>The periodic refresh: everything except the metrics, which the stream keeps current.</summary>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _error = null;
        if (await CallAsync(() => Api.GetStatusAsync(cancellationToken)) is { } status)
        {
            _status = status;
        }

        if (await CallAsync(() => Api.GetLinkClientsAsync(cancellationToken)) is { } clients)
        {
            _clients = [.. clients.Clients];
        }

        await LoadRunsAndEventsAsync(cancellationToken);
        await InvokeAsync(StateHasChanged);
    }

    private async Task<T?> CallAsync<T>(Func<Task<T>> call)
        where T : class
    {
        try
        {
            return await call();
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            // The session ended while the page was open; the session guard sends the user to the login page.
            _unauthorized = true;
            Session.MarkSignedOut();
            return null;
        }
        catch (ApiException exception)
        {
            _error = exception;
            return null;
        }
    }
}
