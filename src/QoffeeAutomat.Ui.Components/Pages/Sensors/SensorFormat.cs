using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Sensors;

/// <summary>How the sensors pages show a sensor and its value.</summary>
public static class SensorFormat
{
    /// <summary>The metric id of a custom sensor.</summary>
    /// <param name="sensorId">The sensor id.</param>
    /// <returns><c>custom.&lt;id&gt;</c>.</returns>
    public static string MetricId(string sensorId)
    {
        ArgumentNullException.ThrowIfNull(sensorId);

        return "custom." + sensorId;
    }

    /// <summary>Formats a value of a sensor with its precision and unit, the way the metric lists do.</summary>
    /// <param name="sensor">The sensor.</param>
    /// <param name="value">The value; <c>null</c> when unavailable.</param>
    /// <param name="localizer">The texts and the culture.</param>
    /// <returns>The text.</returns>
    public static string Value(SensorDocument sensor, JsonElement? value, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(localizer);

        var metric = new MetricInfo(
            MetricId(sensor.Id),
            sensor.Name,
            sensor.Kind,
            sensor.ValueType,
            null,
            sensor.Unit,
            sensor.DeviceClass,
            sensor.StateClass,
            sensor.EntityCategory,
            sensor.Icon,
            sensor.Precision,
            sensor.Options,
            sensor.EnabledDefault,
            sensor.Enabled,
            sensor.ExposeToHa,
            sensor.IntervalSeconds ?? 0,
            sensor.Deadband,
            Custom: true,
            HasValue: value is not null,
            Value: value);
        return DashboardFormat.MetricValue(metric, value, localizer);
    }
}
