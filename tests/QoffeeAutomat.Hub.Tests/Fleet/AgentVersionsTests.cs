using QoffeeAutomat.Hub.Fleet.Actions;

namespace QoffeeAutomat.Hub.Tests.Fleet;

/// <summary>How the hub reads and compares the versions that agents report (T917): the update of agents waits for "the target version or newer", so the order has to be right.</summary>
[Trait("Category", "FleetActions")]
public sealed class AgentVersionsTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3+build5", "1.2.3", 0)]
    [InlineData("1.2.3-rc.1", "1.2.3", -1)]
    [InlineData("1.2.3", "1.2.3-rc.1", 1)]
    [InlineData("1.2.3-rc.2", "1.2.3-rc.10", -1)]
    [InlineData("1.2.3-alpha", "1.2.3-beta", -1)]
    [InlineData("1.2.3-1", "1.2.3-alpha", -1)]
    [InlineData("1.2.3-rc", "1.2.3-rc.1", -1)]
    public void Versions_are_ordered_as_semantic_versions(string left, string right, int expected)
    {
        int? result = AgentVersions.Compare(left, right);

        Assert.NotNull(result);
        Assert.Equal(expected, Math.Sign(result.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.-3")]
    [InlineData("latest")]
    public void Text_that_is_not_a_version_is_not_one_and_compares_with_nothing(string? text)
    {
        Assert.False(AgentVersions.IsValid(text));
        Assert.Null(AgentVersions.Compare(text, "1.0.0"));
        Assert.Null(AgentVersions.Compare("1.0.0", text));
        Assert.False(AgentVersions.AtLeast(text, "1.0.0"));
    }

    [Fact]
    public void An_agent_is_at_least_the_target_when_it_has_the_target_or_a_newer_version()
    {
        Assert.True(AgentVersions.AtLeast("2.0.0", "2.0.0"));
        Assert.True(AgentVersions.AtLeast("2.0.1", "2.0.0"));
        Assert.False(AgentVersions.AtLeast("1.9.9", "2.0.0"));
        Assert.False(AgentVersions.AtLeast("2.0.0-rc.1", "2.0.0"));
    }

    [Fact]
    public void The_newest_of_several_reports_wins_and_garbage_is_ignored()
    {
        string? newest = null;
        foreach (string? report in new[] { "1.0.0", "v1.10.0", "not a version", null, "1.9.0" })
        {
            newest = AgentVersions.Newest(newest, report);
        }

        Assert.Equal("v1.10.0", newest);
        Assert.Null(AgentVersions.Newest(null, "garbage"));
    }
}
