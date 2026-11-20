using System.Text.Json;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Gateway;

namespace QoffeeAutomat.Hub.Tests.Gateway;

/// <summary>
/// How the gateway combines the demands of its consumers into the one subscription of an agent (PLAN.md S07 <c>subscribe.intervals</c>, S20 "Fleet link", T906): a metric that
/// anybody wants live is live, otherwise the shortest interval wins, and the wildcard wins over everything.
/// </summary>
[Trait("Category", "Gateway")]
public sealed class MetricUnionTests
{
    private static KeyValuePair<string, TimeSpan?> Wants(string id, double? seconds = null)
    {
        return new KeyValuePair<string, TimeSpan?>(id, seconds is { } value ? TimeSpan.FromSeconds(value) : null);
    }

    private static MetricDemand Demand(params KeyValuePair<string, TimeSpan?>[] metrics) => MetricDemand.Of(metrics);

    [Fact]
    public void No_consumer_means_no_subscription()
    {
        MetricUnion union = MetricUnion.Compute([]);

        Assert.True(union.IsEmpty);
        Assert.Empty(union.Metrics);
        Assert.Empty(union.Intervals);
        Assert.Equal(string.Empty, union.Key);
        Assert.Same(MetricUnion.Empty, union);
        Assert.True(MetricDemand.None.IsEmpty);
        Assert.True(MetricUnion.Compute([MetricDemand.None]).IsEmpty);
    }

    [Fact]
    public void Two_consumers_with_different_metrics_and_intervals_give_one_union()
    {
        MetricDemand store = Demand(Wants("cpu.usage", 60), Wants("memory.used", 60), Wants("disk.free", 300));
        MetricDemand view = Demand(Wants("cpu.usage", 5), Wants("network.up"));

        MetricUnion union = MetricUnion.Compute([store, view]);

        // Sorted, every metric once.
        Assert.Equal(["cpu.usage", "disk.free", "memory.used", "network.up"], union.Metrics);
        Assert.False(union.All);

        // The shortest interval of the consumers that want an interval; a metric that a consumer wants live has no interval at all.
        Assert.Equal(5, union.Intervals["cpu.usage"]);
        Assert.Equal(60, union.Intervals["memory.used"]);
        Assert.Equal(300, union.Intervals["disk.free"]);
        Assert.False(union.Intervals.ContainsKey("network.up"));
    }

    [Fact]
    public void A_metric_that_any_consumer_wants_live_is_live_in_the_union()
    {
        MetricUnion union = MetricUnion.Compute([Demand(Wants("cpu.usage", 60)), Demand(Wants("cpu.usage"))]);

        Assert.Equal(["cpu.usage"], union.Metrics);
        Assert.Empty(union.Intervals);
    }

    [Fact]
    public void The_wildcard_covers_every_metric_and_leaves_no_intervals()
    {
        MetricUnion union = MetricUnion.Compute([MetricDemand.Everything, Demand(Wants("cpu.usage", 60))]);

        Assert.True(union.All);
        Assert.Empty(union.Metrics);
        Assert.Empty(union.Intervals);
        Assert.False(union.IsEmpty);
        Assert.StartsWith("*", union.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void When_the_last_consumer_of_a_metric_goes_away_the_union_no_longer_has_it()
    {
        MetricDemand store = Demand(Wants("cpu.usage", 60), Wants("memory.used", 60));
        MetricDemand view = Demand(Wants("cpu.usage", 5), Wants("network.up"));

        MetricUnion both = MetricUnion.Compute([store, view]);
        MetricUnion withoutView = MetricUnion.Compute([store]);
        MetricUnion withoutStore = MetricUnion.Compute([view]);

        Assert.Contains("network.up", both.Metrics);
        Assert.DoesNotContain("network.up", withoutView.Metrics);
        Assert.Equal(60, withoutView.Intervals["cpu.usage"]);
        Assert.DoesNotContain("memory.used", withoutStore.Metrics);
        Assert.Equal(5, withoutStore.Intervals["cpu.usage"]);
    }

    [Fact]
    public void Equal_unions_have_equal_keys_whatever_the_order_of_the_consumers()
    {
        MetricDemand first = Demand(Wants("b", 10), Wants("a"));
        MetricDemand second = Demand(Wants("a", 30), Wants("c", 20));

        MetricUnion one = MetricUnion.Compute([first, second]);
        MetricUnion other = MetricUnion.Compute([second, first]);
        MetricUnion different = MetricUnion.Compute([first, Demand(Wants("a", 30), Wants("c", 21))]);

        Assert.Equal(one.Key, other.Key);
        Assert.NotEqual(one.Key, different.Key);
        Assert.NotEqual(one.Key, MetricUnion.Compute([first]).Key);
    }

    [Fact]
    public void The_subscribe_request_carries_the_union()
    {
        MetricUnion union = MetricUnion.Compute([Demand(Wants("cpu.usage", 10), Wants("memory.used"))]);

        using JsonDocument document = JsonDocument.Parse(LinkCodec.Serialize(union.ToMessage(7)));
        JsonElement frame = document.RootElement;

        Assert.Equal(MessageTypes.Subscribe, frame.GetProperty("t").GetString());
        Assert.Equal(7, frame.GetProperty("id").GetInt64());
        Assert.Equal(["cpu.usage", "memory.used"], frame.GetProperty("metrics").EnumerateArray().Select(metric => metric.GetString()));
        JsonElement intervals = frame.GetProperty("intervals");
        Assert.Equal(10, intervals.GetProperty("cpu.usage").GetDouble());
        Assert.False(intervals.TryGetProperty("memory.used", out _));
    }

    [Fact]
    public void The_empty_union_ends_a_subscription_with_an_empty_list_and_the_wildcard_is_a_string()
    {
        using JsonDocument empty = JsonDocument.Parse(LinkCodec.Serialize(MetricUnion.Empty.ToMessage(1)));
        using JsonDocument all = JsonDocument.Parse(LinkCodec.Serialize(MetricUnion.Compute([MetricDemand.Everything]).ToMessage(2)));

        Assert.Equal(JsonValueKind.Array, empty.RootElement.GetProperty("metrics").ValueKind);
        Assert.Equal(0, empty.RootElement.GetProperty("metrics").GetArrayLength());
        Assert.False(empty.RootElement.TryGetProperty("intervals", out _));
        Assert.Equal("*", all.RootElement.GetProperty("metrics").GetString());
        Assert.False(all.RootElement.TryGetProperty("intervals", out _));
    }

    [Fact]
    public void A_metric_listed_twice_in_one_demand_keeps_the_shorter_interval()
    {
        MetricDemand demand = MetricDemand.Of([Wants("a", 60), Wants("a", 10), Wants("b", 5), Wants("b")]);

        Assert.Equal(TimeSpan.FromSeconds(10), demand.Metrics["a"]);
        Assert.Null(demand.Metrics["b"]);
    }

    [Fact]
    public void Live_wants_the_metrics_without_an_interval()
    {
        MetricDemand demand = MetricDemand.Live(["a", "b"]);

        Assert.Equal(2, demand.Metrics.Count);
        Assert.All(demand.Metrics.Values, interval => Assert.Null(interval));
        Assert.Same(MetricDemand.None, MetricDemand.Live([]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nid")]
    public void A_metric_id_must_not_be_empty_or_contain_control_characters(string id)
    {
        Assert.Throws<ArgumentException>(() => MetricDemand.Live([id]));
    }

    [Fact]
    public void A_metric_id_has_a_length_limit()
    {
        Assert.Throws<ArgumentException>(() => MetricDemand.Live([new string('a', MetricDemand.MaxMetricIdLength + 1)]));
        Assert.Single(MetricDemand.Live([new string('a', MetricDemand.MaxMetricIdLength)]).Metrics);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(86401)]
    public void An_interval_must_be_longer_than_zero_and_at_most_a_day(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Demand(Wants("a", seconds)));
    }
}
