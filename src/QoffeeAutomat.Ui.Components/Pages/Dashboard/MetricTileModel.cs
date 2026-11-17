using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Controls;

namespace QoffeeAutomat.Ui.Components.Pages.Dashboard;

/// <summary>One tile of the dashboard: a metric, its current value and the values of the chart.</summary>
public sealed class MetricTileModel
{
    private readonly List<ChartPoint> _points = [];

    /// <summary>Creates the tile.</summary>
    /// <param name="info">The metric.</param>
    public MetricTileModel(MetricInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        Info = info;
        Value = info.Value;
    }

    /// <summary>The metric with its settings.</summary>
    public MetricInfo Info { get; }

    /// <summary>The current value; <c>null</c> when the metric is unavailable or has no value yet.</summary>
    public JsonElement? Value { get; private set; }

    /// <summary>The numeric values of the chart, oldest first.</summary>
    public IReadOnlyList<ChartPoint> Points => _points;

    /// <summary>Replaces the chart with recorded values.</summary>
    /// <param name="points">The recorded values, oldest first; values that are not numbers are skipped.</param>
    public void SetHistory(IEnumerable<MetricPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        _points.Clear();
        foreach (MetricPoint point in points)
        {
            if (TryGetNumber(point.Value, out double number))
            {
                _points.Add(new ChartPoint(point.Timestamp, number));
            }
        }
    }

    /// <summary>Applies a value that arrived on the stream: it becomes the current value and extends the chart.</summary>
    /// <param name="timestamp">When the value was produced.</param>
    /// <param name="value">The value; <c>null</c> kind means unavailable.</param>
    /// <param name="window">How much history the chart keeps.</param>
    /// <param name="maxPoints">The most points the chart keeps.</param>
    public void Apply(DateTimeOffset timestamp, JsonElement value, TimeSpan window, int maxPoints)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            Value = null;
            return;
        }

        Value = value;
        if (!TryGetNumber(value, out double number))
        {
            return;
        }

        // The stream may repeat the snapshot after a reconnect; a point is never added twice for one instant.
        if (_points.Count > 0 && _points[^1].Time >= timestamp)
        {
            return;
        }

        _points.Add(new ChartPoint(timestamp, number));
        DateTimeOffset oldest = timestamp - window;
        int stale = _points.FindIndex(point => point.Time >= oldest);
        if (stale > 0)
        {
            _points.RemoveRange(0, stale);
        }

        if (_points.Count > maxPoints)
        {
            _points.RemoveRange(0, _points.Count - maxPoints);
        }
    }

    /// <summary>Reads a JSON value as a number.</summary>
    /// <param name="value">The value.</param>
    /// <param name="number">The number.</param>
    /// <returns>True when the value is a number.</returns>
    public static bool TryGetNumber(JsonElement? value, out double number)
    {
        if (value is { ValueKind: JsonValueKind.Number } element && element.TryGetDouble(out number))
        {
            return true;
        }

        number = 0;
        return false;
    }
}
