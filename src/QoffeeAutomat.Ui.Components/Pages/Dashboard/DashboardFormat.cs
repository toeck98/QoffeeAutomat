using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Dashboard;

/// <summary>Formatting of the values that the dashboard shows.</summary>
public static class DashboardFormat
{
    private const int MaxSummaryLength = 120;

    /// <summary>Formats a duration in seconds as <c>3d 4h 12m</c>, <c>4h 12m</c>, <c>12m 5s</c> or <c>5s</c>.</summary>
    /// <param name="seconds">The duration.</param>
    /// <returns>The text.</returns>
    public static string Uptime(long seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        string Part(int value, string unit) => value.ToString(CultureInfo.InvariantCulture) + unit;
        if (span.TotalDays >= 1)
        {
            return Part((int)span.TotalDays, "d") + " " + Part(span.Hours, "h") + " " + Part(span.Minutes, "m");
        }

        if (span.TotalHours >= 1)
        {
            return Part(span.Hours, "h") + " " + Part(span.Minutes, "m");
        }

        return span.TotalMinutes >= 1
            ? Part(span.Minutes, "m") + " " + Part(span.Seconds, "s")
            : Part(span.Seconds, "s");
    }

    /// <summary>Formats the duration of a run in milliseconds.</summary>
    /// <param name="milliseconds">The duration.</param>
    /// <param name="culture">The culture of the user.</param>
    /// <returns>The text, for example <c>85 ms</c> or <c>1.2 s</c>.</returns>
    public static string Duration(long milliseconds, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        if (milliseconds < 1000)
        {
            return milliseconds.ToString(culture) + " ms";
        }

        return milliseconds < 60_000
            ? (milliseconds / 1000.0).ToString("0.0", culture) + " s"
            : Uptime(milliseconds / 1000);
    }

    /// <summary>Formats a point in time in the time zone of the browser and the culture of the user.</summary>
    /// <param name="time">The time.</param>
    /// <param name="culture">The culture of the user.</param>
    /// <returns>The text.</returns>
    public static string LocalTime(DateTimeOffset time, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        return time.ToLocalTime().ToString("g", culture);
    }

    /// <summary>Formats the value of a metric with its precision and unit.</summary>
    /// <param name="metric">The metric.</param>
    /// <param name="value">The value; <c>null</c> when unavailable.</param>
    /// <param name="localizer">The texts and the culture.</param>
    /// <returns>The text.</returns>
    public static string MetricValue(MetricInfo metric, JsonElement? value, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(localizer);

        if (value is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return localizer["Common.Unavailable"];
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDouble(out double number):
                int decimals = metric.Precision ?? (number == Math.Floor(number) ? 0 : 1);
                string text = number.ToString("F" + Math.Clamp(decimals, 0, 6).ToString(CultureInfo.InvariantCulture), localizer.Culture);
                return string.IsNullOrEmpty(metric.Unit) ? text : text + " " + metric.Unit;
            case JsonValueKind.True:
                return localizer["Common.On"];
            case JsonValueKind.False:
                return localizer["Common.Off"];
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            default:
                return element.GetRawText();
        }
    }

    /// <summary>Shortens the data of an event to one line for the list.</summary>
    /// <param name="data">The data; may be absent.</param>
    /// <returns>The text; empty when there is no data.</returns>
    public static string EventSummary(JsonElement? data)
    {
        if (data is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        string text = element.GetRawText();
        return text.Length <= MaxSummaryLength ? text : text[..MaxSummaryLength] + "…";
    }
}
