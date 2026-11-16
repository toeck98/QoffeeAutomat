using System.Globalization;
using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Ui.Components.Pages.Metrics;

/// <summary>The metrics of one provider.</summary>
/// <param name="Key">The provider: the part of the metric ids before the first dot, or <see cref="MetricCatalogView.CustomGroup"/>.</param>
/// <param name="Metrics">The metrics, ordered by id.</param>
public sealed record MetricGroup(string Key, IReadOnlyList<MetricInfo> Metrics);

/// <summary>What the metrics page decides about the catalog: the grouping, the privacy hint and the numbers that are typed in.</summary>
public static class MetricCatalogView
{
    /// <summary>The group of the metrics of custom sensors.</summary>
    public const string CustomGroup = "custom";

    /// <summary>The group of a metric id without a dot.</summary>
    public const string OtherGroup = "other";

    /// <summary>Finds the group of a metric: its provider.</summary>
    /// <param name="metric">The metric.</param>
    /// <returns>The group key.</returns>
    public static string GroupOf(MetricInfo metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        if (metric.Custom)
        {
            return CustomGroup;
        }

        int dot = metric.Id.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? metric.Id[..dot] : OtherGroup;
    }

    /// <summary>Groups the catalog by provider: alphabetical, with the custom sensors last.</summary>
    /// <param name="metrics">The catalog.</param>
    /// <returns>The groups.</returns>
    public static IReadOnlyList<MetricGroup> Group(IEnumerable<MetricInfo> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        return
        [
            .. metrics
                .GroupBy(GroupOf, StringComparer.Ordinal)
                .OrderBy(group => string.Equals(group.Key, CustomGroup, StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new MetricGroup(group.Key, [.. group.OrderBy(metric => metric.Id, StringComparer.Ordinal)])),
        ];
    }

    /// <summary>
    /// Tells whether a metric reveals what the user does or who the user is (the active window, the user name), so that the
    /// page warns before it is switched on or shown to Home Assistant. The agent switches such metrics off by default (PLAN.md S06).
    /// </summary>
    /// <param name="metric">The metric.</param>
    /// <returns>True for a privacy-sensitive metric.</returns>
    public static bool IsSensitive(MetricInfo metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        return metric.Id.StartsWith("window.", StringComparison.Ordinal)
            || string.Equals(metric.Id, "session.user", StringComparison.Ordinal)
            || (metric.EnabledDefault == false && string.Equals(metric.Context, "user", StringComparison.Ordinal));
    }

    /// <summary>Reads a non-negative number that the user typed; an empty text is no number.</summary>
    /// <param name="text">The text of the field.</param>
    /// <param name="value">The number.</param>
    /// <returns>True when the text is a finite number that is not negative.</returns>
    public static bool TryParseNonNegative(string? text, out double value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text)
            && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value)
            && value >= 0;
    }

    /// <summary>Writes a number the way an input field wants it.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The text; empty for <c>null</c>.</returns>
    public static string Format(double? value)
    {
        return value is { } number ? number.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
    }
}
