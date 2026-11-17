using QoffeeAutomat.Agent.Api.Prometheus;

namespace QoffeeAutomat.Agent.Tests.Api.Prometheus;

/// <summary>The Prometheus text format of the export (T709) and the strict parser that the tests use to judge it.</summary>
[Trait("Category", "Prometheus")]
public sealed class PrometheusFormatTests
{
    private static PrometheusSample Sample(string id, double value, string name = "A metric", string? unit = null) => new(id, name, unit, value);

    [Theory]
    [InlineData("cpu.usage", "qoffeeautomat_cpu_usage")]
    [InlineData("disk.c.free", "qoffeeautomat_disk_c_free")]
    [InlineData("custom.printer_state", "qoffeeautomat_custom_printer_state")]
    [InlineData("custom.a-b", "qoffeeautomat_custom_a_b")]
    public void A_metric_id_becomes_a_prometheus_name_with_the_product_prefix(string id, string expected)
    {
        Assert.Equal(expected, PrometheusFormat.MetricName(id));
    }

    [Fact]
    public void The_output_is_valid_exposition_text_with_a_gauge_per_metric_and_the_agent_labels()
    {
        string text = PrometheusFormat.Write(
            [Sample("cpu.usage", 12.5, "CPU usage", "%"), Sample("session.locked", 1, "Session locked")],
            "agent-1",
            "Gaming-PC");

        IReadOnlyList<ExpositionFamily> families = ExpositionParser.Parse(text);

        Assert.Equal(["qoffeeautomat_cpu_usage", "qoffeeautomat_session_locked"], families.Select(family => family.Name));
        Assert.All(families, family => Assert.Equal("gauge", family.Type));
        ExpositionFamily cpu = families[0];
        Assert.Equal("CPU usage (%)", cpu.Help);
        ExpositionSample sample = Assert.Single(cpu.Samples);
        Assert.Equal(12.5, sample.Value);
        Assert.Equal("agent-1", sample.Labels["agent_id"]);
        Assert.Equal("Gaming-PC", sample.Labels["agent"]);
        Assert.Equal(2, sample.Labels.Count);
        Assert.Null(sample.Timestamp);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_samples_are_ordered_by_metric_id()
    {
        string text = PrometheusFormat.Write([Sample("b.one", 1), Sample("a.one", 2), Sample("c.one", 3)], "id", "name");

        Assert.Equal(["qoffeeautomat_a_one", "qoffeeautomat_b_one", "qoffeeautomat_c_one"], ExpositionParser.Parse(text).Select(family => family.Name));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(1e-7)]
    [InlineData(123456789.125)]
    [InlineData(1e21)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void A_number_is_written_so_that_it_reads_back_exactly(double value)
    {
        string text = PrometheusFormat.Write([Sample("m", value)], "id", "name");

        Assert.Equal(value, Assert.Single(Assert.Single(ExpositionParser.Parse(text)).Samples).Value);
    }

    [Fact]
    public void A_value_that_is_not_a_finite_number_is_left_out()
    {
        string text = PrometheusFormat.Write([Sample("nan", double.NaN), Sample("inf", double.PositiveInfinity), Sample("fine", 1)], "id", "name");

        Assert.Equal(["qoffeeautomat_fine"], ExpositionParser.Parse(text).Select(family => family.Name));
    }

    [Fact]
    public void A_second_metric_with_the_same_prometheus_name_is_left_out_so_that_there_is_no_duplicate_series()
    {
        string text = PrometheusFormat.Write([Sample("a.b", 1), Sample("a_b", 2)], "id", "name");

        ExpositionFamily family = Assert.Single(ExpositionParser.Parse(text));

        // "a.b" sorts before "a_b" and keeps the name.
        Assert.Equal(1, Assert.Single(family.Samples).Value);
    }

    [Fact]
    public void Labels_and_help_texts_are_escaped_and_read_back()
    {
        string text = PrometheusFormat.Write(
            [Sample("m", 1, "Back\\slash and\nnew line")],
            "id",
            "The \"best\" PC\\1\nline two");

        ExpositionFamily family = Assert.Single(ExpositionParser.Parse(text));

        Assert.Equal("Back\\slash and\nnew line", family.Help);
        Assert.Equal("The \"best\" PC\\1\nline two", Assert.Single(family.Samples).Labels["agent"]);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_to_export_is_an_empty_but_valid_output()
    {
        string text = PrometheusFormat.Write([], "id", "name");

        Assert.Empty(text);
        Assert.Empty(ExpositionParser.Parse(text));
    }

    // The parser itself: it must refuse what a Prometheus server refuses, or the tests above prove little.

    [Fact]
    public void The_parser_reads_a_typical_server_output()
    {
        const string text = """
            # HELP http_requests_total The total number of HTTP requests.
            # TYPE http_requests_total counter
            http_requests_total{method="post",code="200"} 1027 1395066363000
            http_requests_total{method="post",code="400"}    3 1395066363000

            # A comment
            # TYPE temperature gauge
            temperature -3.00e+2
            # TYPE ratio gauge
            ratio{path="C:\\DIR\\FILE.TXT",note="say \"hi\"\n"} +Inf
            """ + "\n";

        IReadOnlyList<ExpositionFamily> families = ExpositionParser.Parse(text);

        Assert.Equal(["http_requests_total", "temperature", "ratio"], families.Select(family => family.Name));
        Assert.Equal(2, families[0].Samples.Count);
        Assert.Equal(1395066363000, families[0].Samples[0].Timestamp);
        Assert.Equal(-300, families[1].Samples[0].Value);
        Assert.Equal("C:\\DIR\\FILE.TXT", families[2].Samples[0].Labels["path"]);
        Assert.Equal("say \"hi\"\n", families[2].Samples[0].Labels["note"]);
        Assert.True(double.IsPositiveInfinity(families[2].Samples[0].Value));
    }

    [Theory]
    [InlineData("a 1", "line feed")]
    [InlineData("1a 1\n", "metric name")]
    [InlineData("a-b 1\n", "space and the value")]
    [InlineData("a one\n", "not a float")]
    [InlineData("a 0x10\n", "not a float")]
    [InlineData("a\n", "space and the value")]
    [InlineData("a 1 2 3\n", "optionally a timestamp")]
    [InlineData("a 1 soon\n", "timestamp")]
    [InlineData("a{b=\"c} 1\n", "unterminated")]
    [InlineData("a{b=c} 1\n", "must be followed")]
    [InlineData("a{1b=\"c\"} 1\n", "label name")]
    [InlineData("a{b=\"\\x\"} 1\n", "unknown escape")]
    [InlineData("a{b=\"1\",b=\"2\"} 1\n", "twice")]
    [InlineData("a{b=\"1\"} 1\na{b=\"1\"} 2\n", "twice")]
    [InlineData("# TYPE a gauge\n# TYPE a gauge\na 1\n", "second TYPE")]
    [InlineData("# HELP a x\n# HELP a y\na 1\n", "second HELP")]
    [InlineData("a 1\n# TYPE a gauge\n", "after its first sample")]
    [InlineData("# TYPE a sometimes\na 1\n", "type must be")]
    [InlineData("# HELP a bad \\q escape\na 1\n", "unknown escape")]
    [InlineData("a 1\nb 1\na{x=\"y\"} 2\n", "together")]
    public void The_parser_refuses_what_a_prometheus_server_refuses(string text, string reason)
    {
        FormatException exception = Assert.Throws<FormatException>(() => ExpositionParser.Parse(text));

        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }
}
