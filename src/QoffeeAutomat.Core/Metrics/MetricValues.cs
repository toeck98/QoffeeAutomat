using System.Globalization;

namespace QoffeeAutomat.Core.Metrics;

/// <summary>Normalisation and comparison of metric values (PLAN.md S06, S07).</summary>
internal static class MetricValues
{
    /// <summary>
    /// Turns whatever a provider returned into one of <see cref="double"/>, <see cref="bool"/>,
    /// <see cref="string"/> or null. Numbers are rounded to <paramref name="precision"/> decimals;
    /// NaN, infinities and unsupported types become null (unavailable).
    /// </summary>
    public static object? Normalize(object? raw, int? precision)
    {
        switch (raw)
        {
            case null:
                return null;
            case bool flag:
                return flag;
            case string text:
                return text;
            case DateTimeOffset moment:
                return FormatTimestamp(moment);
            case DateTime moment:
                return FormatTimestamp(new DateTimeOffset(moment.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(moment, DateTimeKind.Utc) : moment));
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                double number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                if (!double.IsFinite(number))
                {
                    return null;
                }

                return precision is { } digits and >= 0 and <= 15
                    ? Math.Round(number, digits, MidpointRounding.AwayFromZero)
                    : number;
            default:
                return null;
        }
    }

    /// <summary>
    /// Decides which value is published after a sample. Without deadband a different value is
    /// published; with deadband a number must differ by at least the deadband from the published
    /// one. The change from or to "unavailable" is always published.
    /// </summary>
    public static bool IsPublishableChange(object? published, object? sampled, double? deadband)
    {
        if (Same(published, sampled))
        {
            return false;
        }

        if (deadband is { } band && published is double previous && sampled is double next)
        {
            return Math.Abs(next - previous) >= band;
        }

        return true;
    }

    /// <summary>Equality of two normalised values.</summary>
    public static bool Same(object? left, object? right)
    {
        return left switch
        {
            null => right is null,
            double a => right is double b && a.Equals(b),
            bool a => right is bool b && a == b,
            string a => right is string b && string.Equals(a, b, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string FormatTimestamp(DateTimeOffset moment)
    {
        return moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}

/// <summary>The recent samples of one metric: a bounded queue that drops the oldest samples by age and by count.</summary>
internal sealed class MetricRingBuffer
{
    private readonly Queue<MetricSample> _samples = new();
    private readonly TimeSpan _retention;
    private readonly int _capacity;

    public MetricRingBuffer(TimeSpan retention, int capacity)
    {
        _retention = retention;
        _capacity = capacity;
    }

    public int Count => _samples.Count;

    public void Add(DateTimeOffset timestamp, object? value)
    {
        _samples.Enqueue(new MetricSample(timestamp, value));
        while (_samples.Count > _capacity)
        {
            _samples.Dequeue();
        }

        DateTimeOffset oldest = timestamp - _retention;
        while (_samples.Count > 0 && _samples.Peek().Timestamp < oldest)
        {
            _samples.Dequeue();
        }
    }

    public MetricSample[] Snapshot(DateTimeOffset? since)
    {
        return since is { } start
            ? [.. _samples.Where(sample => sample.Timestamp >= start)]
            : [.. _samples];
    }
}
