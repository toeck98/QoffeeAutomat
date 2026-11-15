using System.Text.Json;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Tests;

public class ManifestRevisionTests
{
    private const string ManifestFixture = "manifest.json";

    [Fact]
    public void Computed_rev_of_the_typed_fixture_manifest_equals_the_stored_value()
    {
        Manifest manifest = LinkCodec.ParseManifest(RepositoryPaths.ReadFixture(ManifestFixture));

        Assert.Equal(manifest.Rev, ManifestRevision.Compute(manifest));
        Assert.True(ManifestRevision.IsCurrent(manifest));
    }

    [Fact]
    public void Computed_rev_of_the_raw_fixture_json_equals_the_stored_value()
    {
        JsonElement raw = RepositoryPaths.ReadFixtureElement(ManifestFixture);

        Assert.Equal(raw.GetProperty("rev").GetString(), ManifestRevision.Compute(raw));
    }

    [Fact]
    public void The_rev_is_twelve_lower_case_hex_characters()
    {
        string rev = ManifestRevision.Compute(LinkCodec.ParseManifest(RepositoryPaths.ReadFixture(ManifestFixture)));

        Assert.Equal(ManifestRevision.Length, rev.Length);
        Assert.All(rev, character => Assert.True(char.IsAsciiHexDigitLower(character)));
    }

    [Fact]
    public void The_stored_rev_does_not_influence_the_computed_rev()
    {
        Manifest manifest = LinkCodec.ParseManifest(RepositoryPaths.ReadFixture(ManifestFixture));

        Assert.Equal(ManifestRevision.Compute(manifest), ManifestRevision.Compute(manifest with { Rev = "000000000000" }));
    }

    [Fact]
    public void A_change_to_the_manifest_changes_the_rev()
    {
        Manifest manifest = LinkCodec.ParseManifest(RepositoryPaths.ReadFixture(ManifestFixture));
        Manifest renamed = manifest with { Agent = manifest.Agent with { Name = "Other-PC" } };

        Assert.NotEqual(ManifestRevision.Compute(manifest), ManifestRevision.Compute(renamed));
    }

    [Fact]
    public void The_order_of_metrics_matters_because_arrays_keep_their_order()
    {
        Manifest manifest = LinkCodec.ParseManifest(RepositoryPaths.ReadFixture(ManifestFixture));
        Manifest reversed = manifest with { Metrics = manifest.Metrics.Reverse().ToArray() };

        Assert.NotEqual(ManifestRevision.Compute(manifest), ManifestRevision.Compute(reversed));
    }

    [Fact]
    public void WithRevision_sets_the_computed_rev()
    {
        Manifest manifest = LinkCodec.ParseManifest(RepositoryPaths.ReadFixture(ManifestFixture));
        Manifest stale = manifest with { Rev = "ffffffffffff" };

        Assert.False(ManifestRevision.IsCurrent(stale));
        Assert.Equal(manifest.Rev, ManifestRevision.WithRevision(stale).Rev);
    }

    [Fact]
    public void Members_with_null_do_not_change_the_rev()
    {
        string text = RepositoryPaths.ReadFixture(ManifestFixture);
        const string Plain = "\"control\": { \"type\": \"button\" }";
        const string WithNull = "\"control\": { \"type\": \"button\", \"state_metric\": null }";
        Assert.Contains(Plain, text, StringComparison.Ordinal);

        using JsonDocument original = JsonDocument.Parse(text);
        using JsonDocument withNull = JsonDocument.Parse(text.Replace(Plain, WithNull, StringComparison.Ordinal));

        Assert.Equal(ManifestRevision.Compute(original.RootElement), ManifestRevision.Compute(withNull.RootElement));
    }

    [Fact]
    public void The_order_of_members_in_the_json_does_not_change_the_rev()
    {
        using JsonDocument first = JsonDocument.Parse("""{"b":1,"a":{"y":[1,2],"x":"s"},"rev":"aaaaaaaaaaaa"}""");
        using JsonDocument second = JsonDocument.Parse("""{ "rev": "bbbbbbbbbbbb", "a": { "x": "s", "y": [1, 2] }, "b": 1 }""");

        Assert.Equal(ManifestRevision.Compute(first.RootElement), ManifestRevision.Compute(second.RootElement));
    }
}
