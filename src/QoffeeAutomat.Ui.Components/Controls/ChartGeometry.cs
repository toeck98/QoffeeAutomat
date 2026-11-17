using System.Globalization;

namespace QoffeeAutomat.Ui.Components.Controls;

/// <summary>One point of a line chart.</summary>
/// <param name="Time">When the value was sampled.</param>
/// <param name="Value">The value.</param>
public readonly record struct ChartPoint(DateTimeOffset Time, double Value);

/// <summary>
/// Turns points into the <c>points</c> attribute of an SVG polyline. The charts are drawn by <c>LineChart</c> itself, so
/// they need no charting library and no script; the geometry is plain arithmetic and is tested on its own.
/// </summary>
public static class ChartGeometry
{
    /// <summary>The width of the drawing area; the SVG scales it to the element.</summary>
    public const double Width = 100;

    /// <summary>The height of the drawing area.</summary>
    public const double Height = 40;

    private const double Padding = 2;

    /// <summary>Calculates the polyline of points that are ordered by time.</summary>
    /// <param name="points">The points, oldest first.</param>
    /// <returns>The value of the <c>points</c> attribute; empty when there are fewer than two points.</returns>
    public static string Polyline(IReadOnlyList<ChartPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 2)
        {
            return string.Empty;
        }

        double minimum = double.MaxValue;
        double maximum = double.MinValue;
        foreach (ChartPoint point in points)
        {
            minimum = Math.Min(minimum, point.Value);
            maximum = Math.Max(maximum, point.Value);
        }

        long first = points[0].Time.UtcTicks;
        double span = points[^1].Time.UtcTicks - first;
        double range = maximum - minimum;
        var builder = new System.Text.StringBuilder();
        for (int index = 0; index < points.Count; index++)
        {
            ChartPoint point = points[index];
            double x = span <= 0 ? Width * index / (points.Count - 1) : Width * (point.Time.UtcTicks - first) / span;
            double y = range <= 0
                ? Height / 2
                : Height - Padding - ((point.Value - minimum) / range * (Height - (2 * Padding)));
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(x.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(y.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
