using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Tests.Config;
using static QoffeeAutomat.Core.Tests.Manifest.ManifestTestSupport;
using LinkManifest = QoffeeAutomat.Contracts.Link.Manifest;

namespace QoffeeAutomat.Core.Tests.Manifest;

/// <summary>The cached manifest and the manifest-changed notification (PLAN.md T209).</summary>
[Trait("Category", "Manifest")]
public sealed class ManifestServiceTests
{
    private static CommandDocument Exposed(string id = "lock_session") =>
        Command(id, control: new HaControl(HaControlTypes.Button));

    [Fact]
    public async Task The_first_manifest_is_built_at_once_without_a_notification()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();

        LinkManifest manifest = harness.Service.Current;

        Assert.Matches("^[0-9a-f]{12}$", manifest.Rev);
        Assert.Equal(manifest.Rev, harness.Service.Rev);
        Assert.Equal(0, harness.ChangeCount);
        Assert.Contains(manifest.Metrics, metric => metric.Id == "test.value");
        Assert.Equal(["notify"], manifest.Features);
    }

    [Fact]
    public async Task The_manifest_is_cached_until_an_input_changes()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();

        LinkManifest first = harness.Service.Current;
        LinkManifest second = harness.Service.Current;

        Assert.Same(first, second);
    }

    [Fact]
    public async Task A_saved_command_is_in_the_manifest_the_moment_it_is_read_even_before_the_service_is_started()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        string before = harness.Service.Current.Rev;

        await harness.SaveAsync(Exposed());

        LinkManifest after = harness.Service.Current;
        Assert.NotEqual(before, after.Rev);
        Assert.Equal(["lock_session"], after.Commands.Select(command => command.Id));
        Assert.Equal(1, harness.ChangeCount);
        Assert.Equal(before, harness.Changes[0].PreviousRev);
        Assert.Equal(after.Rev, harness.Changes[0].Rev);
    }

    [Fact]
    public async Task Enabling_disabling_and_exposing_a_command_changes_the_revision_through_the_store()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        await harness.SaveAsync(Exposed());
        string enabled = harness.Service.Current.Rev;

        await harness.SaveAsync(Exposed() with { Enabled = false });
        string disabled = harness.Service.Current.Rev;
        await harness.SaveAsync(Exposed());
        string enabledAgain = harness.Service.Current.Rev;
        await harness.SaveAsync(Exposed() with { ExposeToHa = false });
        string hidden = harness.Service.Current.Rev;

        Assert.NotEqual(enabled, disabled);
        Assert.Equal(enabled, enabledAgain);
        Assert.Equal(disabled, hidden);
        Assert.Empty(harness.Service.Current.Commands);
    }

    [Fact]
    public async Task Editing_only_the_script_does_not_change_the_revision_and_raises_nothing()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        await harness.SaveAsync(Exposed());
        string rev = harness.Service.Current.Rev;
        int changes = harness.ChangeCount;

        await harness.SaveAsync(Exposed().WithCode("Write-Output 'edited script body'"));
        await harness.SaveAsync(Exposed() with { TimeoutSeconds = 5, Concurrency = ConcurrencyModes.Parallel });

        Assert.Equal(rev, harness.Service.Current.Rev);
        Assert.Equal(changes, harness.ChangeCount);
    }

    [Fact]
    public async Task Editing_the_parameters_of_a_command_changes_the_revision_through_the_store()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        CommandDocument profile = Command("set_profile", parameters: [Choice("Profile", "A", "B")], control: Select("Profile"));
        await harness.SaveAsync(profile);
        string rev = harness.Service.Current.Rev;

        await harness.SaveAsync(profile with { Parameters = [Choice("Profile", "A", "B", "C")] });

        Assert.NotEqual(rev, harness.Service.Current.Rev);
    }

    [Fact]
    public async Task The_started_service_raises_manifest_changed_when_a_command_is_saved()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        harness.Registry.Start();
        harness.Service.Start();
        string before = harness.Service.Rev;
        Task<ManifestChangedEventArgs> next = harness.NextChangeAsync();

        await harness.SaveAsync(Exposed());

        ManifestChangedEventArgs change = await next;
        Assert.Equal(before, change.PreviousRev);
        Assert.NotEqual(before, change.Rev);
        Assert.Equal(harness.Service.Current.Rev, change.Rev);
        Assert.Contains(change.Manifest.Commands, command => command.Id == "lock_session");
    }

    [Fact]
    public async Task The_started_service_stays_quiet_when_only_the_script_was_edited()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        harness.Registry.Start();
        harness.Service.Start();
        Task<ManifestChangedEventArgs> first = harness.NextChangeAsync();
        await harness.SaveAsync(Exposed());
        await first;
        int changes = harness.ChangeCount;

        Task registry = harness.NextRegistryChangeAsync();
        await harness.SaveAsync(Exposed().WithCode("'only the script changed'"));
        await registry;

        // The service subscribed to the registry first, so it has handled this change by now.
        Assert.Equal(changes, harness.ChangeCount);
    }

    [Fact]
    public async Task The_started_service_raises_manifest_changed_when_a_command_is_disabled()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        harness.Registry.Start();
        harness.Service.Start();
        Task<ManifestChangedEventArgs> added = harness.NextChangeAsync();
        await harness.SaveAsync(Exposed());
        await added;
        Task<ManifestChangedEventArgs> next = harness.NextChangeAsync();

        await harness.SaveAsync(Exposed() with { Enabled = false });

        ManifestChangedEventArgs change = await next;
        Assert.Empty(change.Manifest.Commands);
    }

    [Fact]
    public async Task A_new_exposed_sensor_changes_the_revision_and_an_unexposed_one_does_not()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        harness.Service.Start();
        string baseline = harness.Service.Current.Rev;

        await harness.SaveSensorAsync(ConfigTestSupport.Sensor("hidden_sensor") with { ExposeToHa = false });
        await harness.Engine.PollAsync(CancellationToken.None);
        Assert.Equal(baseline, harness.Service.Current.Rev);
        Assert.Equal(0, harness.ChangeCount);

        await harness.SaveSensorAsync(ConfigTestSupport.Sensor("shown_sensor") with { ExposeToHa = true });
        await harness.Engine.PollAsync(CancellationToken.None);

        Assert.NotEqual(baseline, harness.Service.Current.Rev);
        Assert.Contains(harness.Service.Current.Metrics, metric => metric.Id == "custom.shown_sensor");
        Assert.DoesNotContain(harness.Service.Current.Metrics, metric => metric.Id == "custom.hidden_sensor");
        Assert.Equal(1, harness.ChangeCount);
    }

    [Fact]
    public async Task Changing_the_agent_name_in_the_settings_changes_the_revision_and_notifies()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync(watchSettings: true);
        harness.Service.Start();
        string before = harness.Service.Current.Rev;
        Task<ManifestChangedEventArgs> next = harness.NextChangeAsync();

        SaveResult<AgentSettingsDocument> saved = await harness.Store.Local.SaveAsync(
            new AgentSettingsDocument(DocumentSchema.CurrentVersion, "Study-PC"),
            null,
            CancellationToken.None);

        Assert.True(saved.IsApplied);
        ManifestChangedEventArgs change = await next;
        Assert.NotEqual(before, change.Rev);
        Assert.Equal("Study-PC", change.Manifest.Agent.Name);
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_stop_the_others_or_the_source()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        var received = new List<string>();
        harness.Service.Changed += (_, _) => throw new InvalidOperationException("a faulty subscriber");
        harness.Service.Changed += (_, change) => received.Add(change.Rev);

        await harness.SaveAsync(Exposed());
        string rev = harness.Service.Current.Rev;

        Assert.Equal([rev], received);
    }

    [Fact]
    public async Task Starting_twice_raises_each_change_once()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        harness.Registry.Start();
        harness.Service.Start();
        harness.Service.Start();
        Task<ManifestChangedEventArgs> next = harness.NextChangeAsync();

        await harness.SaveAsync(Exposed());
        await next;
        Task registry = harness.NextRegistryChangeAsync();
        await harness.SaveAsync(Exposed("another_command"));
        await registry;

        Assert.Equal(2, harness.ChangeCount);
    }

    [Fact]
    public async Task A_disposed_service_stops_watching_its_sources()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        harness.Registry.Start();
        harness.Service.Start();
        harness.Service.Dispose();
        Task registry = harness.NextRegistryChangeAsync();

        await harness.SaveAsync(Exposed());
        await registry;

        Assert.Equal(0, harness.ChangeCount);
        Assert.Throws<ObjectDisposedException>(harness.Service.Start);
    }

    [Fact]
    public async Task The_manifest_of_a_real_catalog_with_the_simulated_platform_validates_against_the_schema()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync(simulatedMetrics: true);
        await harness.SaveAsync(Exposed());
        await harness.SaveAsync(Command(
            "set_profile",
            parameters: [Choice("Profile", "Normal-Mode", "TV-Mode")],
            control: Select("Profile"),
            captureOutput: true));
        await harness.SaveAsync(Command("not_exposed", exposeToHa: false));
        await harness.SaveSensorAsync(ConfigTestSupport.Sensor("watched_app") with { ExposeToHa = true });
        await harness.Engine.PollAsync(CancellationToken.None);

        LinkManifest manifest = harness.Service.Current;

        Assert.Empty(SchemaErrors(manifest));
        Assert.True(ManifestRevision.IsCurrent(manifest));
        Assert.True(manifest.Metrics.Count > 5, "The simulated platform provides many metrics.");
        Assert.Contains(manifest.Metrics, metric => metric.Id == "custom.watched_app");
        Assert.Equal(["lock_session", "set_profile"], manifest.Commands.Select(command => command.Id));
        Assert.NotEmpty(manifest.Events!);
    }
}
