namespace QoffeeAutomat.Hub.Metrics;

/// <summary>The value types of a metric in the manifest of an agent (PLAN.md S06 <c>valueType</c>), as the hub stores them.</summary>
public static class MetricValueTypes
{
    /// <summary>A number; stored in <c>metric_raw</c> and rolled up.</summary>
    public const string Number = "number";

    /// <summary>A boolean; stored as 0 or 1 in <c>metric_raw</c> and rolled up (the average is the share of time it was true).</summary>
    public const string Bool = "bool";

    /// <summary>A text; stored as a change log in <c>metric_states</c>.</summary>
    public const string Text = "string";

    /// <summary>A time stamp as text; stored as a change log.</summary>
    public const string Timestamp = "timestamp";

    /// <summary>One of a list of texts; stored as a change log.</summary>
    public const string Choice = "enum";

    /// <summary>Tells whether values of the type are stored as numbers (raw samples and roll-ups).</summary>
    /// <param name="valueType">The value type.</param>
    /// <returns>True for <see cref="Number"/> and <see cref="Bool"/>.</returns>
    public static bool IsNumeric(string valueType) => valueType is Number or Bool;

    /// <summary>Tells whether a text is a value type that the hub stores.</summary>
    /// <param name="valueType">The text.</param>
    /// <returns>True for the five types.</returns>
    public static bool IsKnown(string valueType) => valueType is Number or Bool or Text or Timestamp or Choice;
}

/// <summary>The resolutions in which the hub keeps numeric metrics (PLAN.md S20 "Metrics on the hub").</summary>
public enum MetricResolution
{
    /// <summary>Every stored sample.</summary>
    Raw = 0,

    /// <summary>Minimum, average, maximum and count per five minutes.</summary>
    FiveMinutes = 1,

    /// <summary>Minimum, average, maximum and count per hour.</summary>
    OneHour = 2,
}

/// <summary>Names and sizes of the <see cref="MetricResolution"/> values.</summary>
public static class MetricResolutions
{
    /// <summary>The width of a five minute bucket in milliseconds.</summary>
    public const long FiveMinutesMilliseconds = 5 * 60 * 1000L;

    /// <summary>The width of a one hour bucket in milliseconds.</summary>
    public const long OneHourMilliseconds = 60 * 60 * 1000L;

    /// <summary>The name of a resolution in the API: <c>raw</c>, <c>5m</c> or <c>1h</c>.</summary>
    /// <param name="resolution">The resolution.</param>
    /// <returns>The name.</returns>
    public static string ToText(MetricResolution resolution)
    {
        return resolution switch
        {
            MetricResolution.Raw => "raw",
            MetricResolution.FiveMinutes => "5m",
            MetricResolution.OneHour => "1h",
            _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Not a resolution."),
        };
    }

    /// <summary>Reads the name of a resolution.</summary>
    /// <param name="text">The name (<c>raw</c>, <c>5m</c> or <c>1h</c>).</param>
    /// <param name="resolution">The resolution.</param>
    /// <returns>True when the text names a resolution.</returns>
    public static bool TryParse(string? text, out MetricResolution resolution)
    {
        switch (text)
        {
            case "raw":
                resolution = MetricResolution.Raw;
                return true;
            case "5m":
                resolution = MetricResolution.FiveMinutes;
                return true;
            case "1h":
                resolution = MetricResolution.OneHour;
                return true;
            default:
                resolution = MetricResolution.Raw;
                return false;
        }
    }

    /// <summary>The width of a bucket of a roll-up.</summary>
    /// <param name="resolution">The resolution; not <see cref="MetricResolution.Raw"/>.</param>
    /// <returns>The width in milliseconds.</returns>
    public static long BucketMilliseconds(MetricResolution resolution)
    {
        return resolution switch
        {
            MetricResolution.FiveMinutes => FiveMinutesMilliseconds,
            MetricResolution.OneHour => OneHourMilliseconds,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Raw samples have no buckets."),
        };
    }

    /// <summary>The start of the bucket that contains a time.</summary>
    /// <param name="milliseconds">The unix time in milliseconds.</param>
    /// <param name="bucket">The width of the bucket in milliseconds.</param>
    /// <returns>The start of the bucket; times before 1970 round down.</returns>
    public static long Floor(long milliseconds, long bucket) => Math.DivRem(milliseconds, bucket, out long rest) * bucket - (rest < 0 ? bucket : 0);
}

/// <summary>One value that an agent reported and the hub wants to keep.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="MetricId">The metric id.</param>
/// <param name="ValueType">One of <see cref="MetricValueTypes"/>.</param>
/// <param name="Unit">The unit from the manifest, or null.</param>
/// <param name="Ts">The unix time in milliseconds.</param>
/// <param name="Number">The value of a numeric metric (0 or 1 for a boolean); unused for a text.</param>
/// <param name="Text">The value of a string, enum or timestamp metric; null for a numeric metric.</param>
public sealed record MetricSample(string AgentId, string MetricId, string ValueType, string? Unit, long Ts, double Number, string? Text = null)
{
    /// <summary>True when the sample goes to the raw samples and the roll-ups; false when it goes to the change log.</summary>
    public bool IsNumeric => Text is null;
}

/// <summary>What <see cref="IMetricStore.AppendAsync"/> did.</summary>
/// <param name="Stored">The samples that were written.</param>
/// <param name="Unchanged">The text samples that were not written because the metric already had that text (the change log keeps changes only).</param>
/// <param name="Skipped">The samples of an agent that does not exist (any more); nothing was written for them.</param>
public sealed record MetricAppendResult(int Stored, int Unchanged, int Skipped);

/// <summary>What <see cref="IMetricStore.RollUpAsync"/> did.</summary>
/// <param name="Buckets5m">The five minute buckets that were written.</param>
/// <param name="Buckets1h">The one hour buckets that were written.</param>
public sealed record MetricRollupResult(int Buckets5m, int Buckets1h);

/// <summary>What <see cref="IMetricStore.ApplyRetentionAsync"/> removed.</summary>
/// <param name="Raw">The raw samples.</param>
/// <param name="States">The entries of the change log.</param>
/// <param name="Rollups5m">The five minute buckets.</param>
/// <param name="Rollups1h">The one hour buckets.</param>
public sealed record MetricRetentionResult(long Raw, long States, long Rollups5m, long Rollups1h)
{
    /// <summary>The number of rows that were removed.</summary>
    public long Total => Raw + States + Rollups5m + Rollups1h;
}

/// <summary>The size of the metric tables.</summary>
/// <param name="Series">The series.</param>
/// <param name="RawRows">The raw samples.</param>
/// <param name="StateRows">The entries of the change log.</param>
/// <param name="Rollup5mRows">The five minute buckets.</param>
/// <param name="Rollup1hRows">The one hour buckets.</param>
/// <param name="DatabaseBytes">The size of the database in bytes, the pages that are free included.</param>
/// <param name="FreeBytes">The part of <paramref name="DatabaseBytes"/> that is free for reuse.</param>
public sealed record MetricStoreStats(long Series, long RawRows, long StateRows, long Rollup5mRows, long Rollup1hRows, long DatabaseBytes, long FreeBytes);

/// <summary>A query for series of the metric store.</summary>
/// <param name="AgentIds">The agents; each must exist.</param>
/// <param name="MetricIds">Metric ids; an entry that ends in <c>*</c> matches every id that starts with the text before it, and <c>*</c> alone matches every metric.</param>
/// <param name="From">The start of the range.</param>
/// <param name="To">The end of the range.</param>
/// <param name="Resolution">The resolution of numeric series. Text series are always a change log.</param>
/// <param name="MaxPoints">The most points per series; a series with more is cut and marked.</param>
public sealed record MetricQuery(
    IReadOnlyList<string> AgentIds,
    IReadOnlyList<string> MetricIds,
    DateTimeOffset From,
    DateTimeOffset To,
    MetricResolution Resolution,
    int MaxPoints);
