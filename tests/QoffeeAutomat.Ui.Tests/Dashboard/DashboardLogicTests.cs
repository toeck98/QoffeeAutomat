using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Controls;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Services;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Dashboard;

/// <summary>The parts of the dashboard that are plain logic: which metrics get a tile, how values are shown, the chart.</summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardLogicTests
{
    [Fact]
    public void The_preferred_metrics_come_first_when_they_are_enabled_and_numeric()
    {
        MetricInfo[] catalog =
        [
            Samples.Metric("disk.c.free", "Free space on C:"),
            Samples.Metric("memory.used_percent", "Memory used"),
            Samples.Metric("cpu.usage", "CPU usage"),
            Samples.Metric("gpu.usage", "GPU usage", enabled: false),
        ];

        IReadOnlyList<MetricInfo> chosen = MetricSelector.Pick(catalog, 6);

        Assert.Equal(["cpu.usage", "memory.used_percent", "disk.c.free"], chosen.Select(metric => metric.Id));
    }

    [Fact]
    public void Disabled_diagnostic_and_non_numeric_metrics_get_no_tile()
    {
        MetricInfo[] catalog =
        [
            Samples.Metric("a", "A", enabled: false),
            Samples.Metric("b", "B", entityCategory: "diagnostic"),
            Samples.Metric("c", "C", valueType: "string"),
            Samples.Metric("d", "D", valueType: "bool"),
            Samples.Metric("e", "E"),
        ];

        IReadOnlyList<MetricInfo> chosen = MetricSelector.Pick(catalog, 6);

        Assert.Equal(["e"], chosen.Select(metric => metric.Id));
    }

    [Fact]
    public void The_number_of_tiles_is_limited()
    {
        MetricInfo[] catalog = [.. Enumerable.Range(1, 10).Select(index => Samples.Metric("m" + index.ToString(CultureInfo.InvariantCulture), "M"))];

        Assert.Equal(3, MetricSelector.Pick(catalog, 3).Count);
        Assert.Empty(MetricSelector.Pick(catalog, 0));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    [InlineData(61, "1m 1s")]
    [InlineData(3_660, "1h 1m")]
    [InlineData(90_000, "1d 1h 0m")]
    [InlineData(-5, "0s")]
    public void An_uptime_is_shown_with_its_two_or_three_largest_units(long seconds, string expected)
    {
        Assert.Equal(expected, DashboardFormat.Uptime(seconds));
    }

    [Theory]
    [InlineData(85L, "85 ms")]
    [InlineData(1_240L, "1.2 s")]
    [InlineData(125_000L, "2m 5s")]
    public void A_duration_is_shown_in_the_unit_that_fits(long milliseconds, string expected)
    {
        Assert.Equal(expected, DashboardFormat.Duration(milliseconds, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_metric_value_is_shown_with_its_precision_and_unit()
    {
        var localizer = new Localizer();
        localizer.SetLanguage("en");
        MetricInfo cpu = Samples.Metric("cpu.usage", "CPU", "%", precision: 1);
        MetricInfo whole = Samples.Metric("count", "Count", unit: "", precision: null);
        MetricInfo fraction = Samples.Metric("ratio", "Ratio", unit: "x", precision: null);

        Assert.Equal("12.5 %", DashboardFormat.MetricValue(cpu, Samples.Number(12.5), localizer));
        Assert.Equal("12.0 %", DashboardFormat.MetricValue(cpu, Samples.Number(12), localizer));
        Assert.Equal("7", DashboardFormat.MetricValue(whole, Samples.Number(7), localizer));
        Assert.Equal("0.5 x", DashboardFormat.MetricValue(fraction, Samples.Number(0.5), localizer));
        Assert.Equal("n/a", DashboardFormat.MetricValue(cpu, null, localizer));
        Assert.Equal("n/a", DashboardFormat.MetricValue(cpu, Samples.Null(), localizer));
        Assert.Equal("hello", DashboardFormat.MetricValue(cpu, Samples.Text("hello"), localizer));
    }

    [Fact]
    public void The_data_of_an_event_is_shortened_to_one_line()
    {
        Assert.Equal(string.Empty, DashboardFormat.EventSummary(null));
        Assert.Equal(string.Empty, DashboardFormat.EventSummary(Samples.Null()));
        Assert.Equal("\"hello\"", DashboardFormat.EventSummary(Samples.Text("hello")));

        string summary = DashboardFormat.EventSummary(Samples.Text(new string('x', 500)));
        Assert.Equal(121, summary.Length);
        Assert.EndsWith("…", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tile_starts_with_the_value_of_the_catalog_and_with_the_recorded_numbers_as_its_chart()
    {
        var tile = new MetricTileModel(Samples.Metric("cpu.usage", "CPU", value: 12.5));

        tile.SetHistory([Samples.Point(-60, 10), new MetricPoint(Samples.Now.AddSeconds(-30), null), Samples.Point(-1, 12.5)]);

        Assert.Equal(12.5, tile.Value!.Value.GetDouble());
        Assert.Equal(2, tile.Points.Count);
    }

    [Fact]
    public void A_value_from_the_stream_becomes_the_current_value_and_extends_the_chart()
    {
        var tile = new MetricTileModel(Samples.Metric("cpu.usage", "CPU"));
        TimeSpan window = TimeSpan.FromMinutes(15);

        tile.Apply(Samples.Now, Samples.Number(1), window, 100);
        tile.Apply(Samples.Now.AddSeconds(2), Samples.Number(2), window, 100);

        Assert.Equal(2, tile.Value!.Value.GetDouble());
        Assert.Equal([1d, 2d], tile.Points.Select(point => point.Value));
    }

    [Fact]
    public void A_repeated_instant_changes_the_value_but_adds_no_second_point()
    {
        var tile = new MetricTileModel(Samples.Metric("cpu.usage", "CPU"));
        TimeSpan window = TimeSpan.FromMinutes(15);

        tile.Apply(Samples.Now, Samples.Number(1), window, 100);
        tile.Apply(Samples.Now, Samples.Number(9), window, 100);

        Assert.Equal(9, tile.Value!.Value.GetDouble());
        Assert.Single(tile.Points);
    }

    [Fact]
    public void Points_older_than_the_window_and_beyond_the_limit_are_dropped()
    {
        var tile = new MetricTileModel(Samples.Metric("cpu.usage", "CPU"));
        TimeSpan window = TimeSpan.FromSeconds(10);

        for (int second = 0; second <= 30; second += 5)
        {
            tile.Apply(Samples.Now.AddSeconds(second), Samples.Number(second), window, 100);
        }

        Assert.Equal(Samples.Now.AddSeconds(20), tile.Points[0].Time);
        Assert.Equal(3, tile.Points.Count);

        var limited = new MetricTileModel(Samples.Metric("cpu.usage", "CPU"));
        for (int second = 0; second < 10; second++)
        {
            limited.Apply(Samples.Now.AddSeconds(second), Samples.Number(second), TimeSpan.FromHours(1), 4);
        }

        Assert.Equal(4, limited.Points.Count);
        Assert.Equal(9d, limited.Points[^1].Value);
    }

    [Fact]
    public void An_unavailable_value_clears_the_current_value_and_keeps_the_chart()
    {
        var tile = new MetricTileModel(Samples.Metric("cpu.usage", "CPU"));
        tile.Apply(Samples.Now, Samples.Number(1), TimeSpan.FromMinutes(1), 100);

        tile.Apply(Samples.Now.AddSeconds(2), Samples.Null(), TimeSpan.FromMinutes(1), 100);

        Assert.Null(tile.Value);
        Assert.Single(tile.Points);
    }

    [Fact]
    public void A_chart_needs_two_points_and_scales_them_into_the_drawing_area()
    {
        DateTimeOffset start = Samples.Now;

        Assert.Equal(string.Empty, ChartGeometry.Polyline([]));
        Assert.Equal(string.Empty, ChartGeometry.Polyline([new ChartPoint(start, 1)]));

        // Time runs left to right over 0..100, the highest value is at the top (y = 2), the lowest at the bottom (y = 38).
        string line = ChartGeometry.Polyline([new ChartPoint(start, 0), new ChartPoint(start.AddSeconds(10), 10), new ChartPoint(start.AddSeconds(20), 5)]);
        Assert.Equal("0,38 50,2 100,20", line);
    }

    [Fact]
    public void A_flat_chart_is_a_line_in_the_middle_and_the_numbers_do_not_depend_on_the_culture()
    {
        if (Globalization.IsInvariant)
        {
            return;
        }

        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            DateTimeOffset start = Samples.Now;

            string line = ChartGeometry.Polyline([new ChartPoint(start, 3), new ChartPoint(start.AddSeconds(3), 3)]);

            Assert.Equal("0,20 100,20", line);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
