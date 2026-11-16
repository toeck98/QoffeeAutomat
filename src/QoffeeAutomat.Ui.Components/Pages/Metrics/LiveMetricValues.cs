using System.Collections.Concurrent;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;

namespace QoffeeAutomat.Ui.Components.Pages.Metrics;

/// <summary>
/// The current values of some metrics for a page that lists them (metrics and sensors pages, PLAN.md D-007): it opens the
/// metric stream of the agent for the given ids, keeps the newest value of each, opens the stream again after it ended or
/// failed, and closes it when it is told to stop or is disposed, so that the agent stops sampling when the page is left.
/// </summary>
public sealed class LiveMetricValues : IAsyncDisposable
{
    private readonly IAgentApi _api;
    private readonly TimeProvider _time;
    private readonly DashboardOptions _options;
    private readonly Func<Task> _changed;
    private readonly Action _unauthorized;
    private readonly ConcurrentDictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
    private LiveFeed? _feed;
    private string[] _ids = [];
    private bool _disposed;

    /// <summary>Creates the holder; nothing is opened until <see cref="SetMetricsAsync"/> is called.</summary>
    /// <param name="api">The agent.</param>
    /// <param name="time">The clock of the reconnect delay.</param>
    /// <param name="options">The stream interval and the reconnect delay.</param>
    /// <param name="changed">Called after a value changed, to render the page again.</param>
    /// <param name="unauthorized">Called when the agent says that the session ended.</param>
    public LiveMetricValues(IAgentApi api, TimeProvider time, DashboardOptions options, Func<Task> changed, Action unauthorized)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(unauthorized);

        _api = api;
        _time = time;
        _options = options;
        _changed = changed;
        _unauthorized = unauthorized;
    }

    /// <summary>The newest value of every metric that has one.</summary>
    public IReadOnlyDictionary<string, JsonElement> Values => _values;

    /// <summary>Streams exactly these metrics from now on; the stream is closed when the list is empty.</summary>
    /// <param name="ids">The metric ids.</param>
    /// <returns>A task that completes when the previous stream is closed and the new one is started.</returns>
    public async Task SetMetricsAsync(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        string[] wanted = [.. ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (_disposed || (_feed is not null && _ids.AsSpan().SequenceEqual(wanted)))
        {
            return;
        }

        await StopAsync();
        _ids = wanted;
        if (wanted.Length > 0 && !_disposed)
        {
            _feed = new LiveFeed(RunAsync);
        }
    }

    /// <summary>Closes the stream and waits until it is closed.</summary>
    /// <returns>A task that completes when the stream is closed.</returns>
    public async Task StopAsync()
    {
        LiveFeed? feed = _feed;
        _feed = null;
        _ids = [];
        if (feed is not null)
        {
            await feed.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        string[] ids = _ids;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (MetricStreamUpdate update in _api.StreamMetricsAsync(ids, _options.StreamIntervalSeconds, cancellationToken))
                {
                    foreach ((string id, JsonElement value) in update.Values)
                    {
                        _values[id] = value;
                    }

                    await _changed();
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
                _unauthorized();
                break;
            }
            catch (ApiException)
            {
                // The agent is not reachable or refused; it is tried again below.
            }

            try
            {
                await Task.Delay(_options.ReconnectDelay, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
