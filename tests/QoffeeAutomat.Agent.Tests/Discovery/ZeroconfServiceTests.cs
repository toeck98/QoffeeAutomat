using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Agent.Discovery;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Discovery;

/// <summary>
/// When the agent is announced (T216), through the whole agent in an in-process host. The recording advertiser of the
/// simulated platform shows what would be announced; the tests that care about sockets use the real responder on a
/// transport that only counts how often it is opened.
/// </summary>
[Trait("Category", "Discovery")]
public sealed class ZeroconfServiceTests : IDisposable
{
    private static readonly ZeroconfOptions FastDebounce = new() { NetworkDebounce = TimeSpan.FromMilliseconds(100) };

    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    private static async Task SaveSettingsAsync(TestAgent agent, string name, bool? zeroconf, LoggingSettings? logging = null)
    {
        IConfigStore store = agent.Services.GetRequiredService<IConfigStore>();
        var settings = new AgentSettingsDocument(DocumentSchema.CurrentVersion, name, Zeroconf: zeroconf, Logging: logging);
        SaveResult<AgentSettingsDocument> result = await store.Local.SaveAsync(settings, null, CancellationToken.None);
        Assert.True(result.IsApplied, string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    private static SimulatedServiceAdvertiser RecorderOf(TestAgent agent) =>
        agent.Services.GetRequiredService<SimulatedPlatform>().Advertiser;

    private static void UseResponderOn(IServiceCollection services, FakeMdnsTransportFactory factory)
    {
        services.RemoveAll<IMdnsTransportFactory>();
        services.AddSingleton<IMdnsTransportFactory>(factory);
        services.RemoveAll<IServiceAdvertiser>();
        services.AddSingleton<MdnsServiceAdvertiser>();
        services.AddSingleton<IServiceAdvertiser>(provider => provider.GetRequiredService<MdnsServiceAdvertiser>());
    }

    private TestAgent CreateAgent(Action<IServiceCollection>? configure = null)
    {
        return new TestAgent(_data.Path, services =>
        {
            services.RemoveAll<ZeroconfOptions>();
            services.AddSingleton(FastDebounce);
            configure?.Invoke(services);
        });
    }

    private void WriteSettings(string name, bool zeroconf)
    {
        string directory = AppPaths.ForRoot(_data.Path).ConfigDirectory;
        Directory.CreateDirectory(directory);
        string json = "{\"schemaVersion\":1,\"revision\":1,\"updatedAt\":\"2026-11-14T09:00:00Z\",\"origin\":\"local\",\"name\":\""
            + name + "\",\"zeroconf\":" + (zeroconf ? "true" : "false") + "}";
        File.WriteAllText(Path.Combine(directory, ConfigFileNames.Settings), json);
    }

    [Fact]
    public async Task The_agent_is_announced_as_soon_as_the_host_accepts_connections_with_id_name_version_and_protocol_range()
    {
        WriteSettings("Study-PC", zeroconf: true);
        await using var agent = CreateAgent();
        SimulatedServiceAdvertiser recorder = RecorderOf(agent);

        await TestWait.UntilAsync(() => recorder.Current is not null, "the announcement");

        ServiceAdvertisement advertisement = recorder.Current!;
        Assert.Equal("_qoffeeautomat._tcp.local.", advertisement.ServiceType);
        Assert.Equal("Study-PC", advertisement.InstanceName);
        Assert.Equal(AgentSettingsDefaults.LinkPort, advertisement.Port);
        string agentId = await agent.Services.GetRequiredService<IPlatformInfo>().GetOrCreateAgentIdAsync(CancellationToken.None);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["id"] = agentId,
                ["name"] = "Study-PC",
                ["v"] = AgentVersion.Current,
                ["pv"] = "1-1",
            },
            advertisement.TxtRecords);
        Assert.Equal(1, recorder.StartCount);
    }

    [Fact]
    public async Task Nothing_is_announced_while_there_is_no_link_listener()
    {
        WriteSettings("Study-PC", zeroconf: true);
        await using var agent = CreateAgent(services =>
        {
            services.RemoveAll<ITlsCertificateSource>();
            services.AddSingleton<ITlsCertificateSource, NoCertificateSource>();
        });
        SimulatedServiceAdvertiser recorder = RecorderOf(agent);

        await Task.Delay(500);

        Assert.Null(recorder.Current);
        Assert.Equal(0, recorder.StartCount);
    }

    [Fact]
    public async Task With_zeroconf_switched_off_no_multicast_socket_is_opened_and_switching_it_on_opens_one()
    {
        WriteSettings("Study-PC", zeroconf: false);
        var factory = new FakeMdnsTransportFactory();
        await using var agent = CreateAgent(services => UseResponderOn(services, factory));
        FakeNetworkChangeSource network = agent.Services.GetRequiredService<FakeNetworkChangeSource>();

        // The host is up and has had time to act on its start; the advertiser was never started.
        await Task.Delay(500);
        Assert.Equal(0, factory.OpenCount);
        int baselineWatchers = network.WatcherCount;
        Assert.False(agent.Services.GetRequiredService<MdnsServiceAdvertiser>().IsAdvertising);

        await SaveSettingsAsync(agent, "Study-PC", zeroconf: true);

        await TestWait.UntilAsync(() => factory.OpenCount == 1, "the multicast transport to be opened");
        FakeMdnsTransport transport = factory.Last!;
        await TestWait.UntilAsync(() => transport.Sent.Count >= 1, "the announcement");
        DecodedMessage announcement = DecodedMessage.Parse(transport.Sent[0].Data);
        DecodedRecord service = Assert.Single(announcement.Answers, record => record.Type == 33);
        Assert.Equal("Study-PC._qoffeeautomat._tcp.local.", service.Name);
        Assert.Equal(AgentSettingsDefaults.LinkPort, service.Port);
        DecodedRecord text = Assert.Single(announcement.Answers, record => record.Type == 16);
        Assert.Contains("name=Study-PC", text.Text);
        Assert.Contains("pv=1-1", text.Text);
        Assert.Contains("v=" + AgentVersion.Current, text.Text);
        Assert.Equal(baselineWatchers + 1, network.WatcherCount);
    }

    [Fact]
    public async Task Switching_zeroconf_off_withdraws_the_advertisement_and_stops_watching_the_network()
    {
        WriteSettings("Study-PC", zeroconf: true);
        var factory = new FakeMdnsTransportFactory();
        await using var agent = CreateAgent(services => UseResponderOn(services, factory));
        MdnsServiceAdvertiser advertiser = agent.Services.GetRequiredService<MdnsServiceAdvertiser>();
        FakeNetworkChangeSource network = agent.Services.GetRequiredService<FakeNetworkChangeSource>();
        await TestWait.UntilAsync(() => advertiser.IsAdvertising, "the announcement");
        FakeMdnsTransport transport = factory.Last!;
        int watchersWhileOn = network.WatcherCount;

        await SaveSettingsAsync(agent, "Study-PC", zeroconf: false);

        await TestWait.UntilAsync(() => !advertiser.IsAdvertising && transport.IsDisposed, "the withdrawal");
        Assert.True(transport.IsDisposed);
        Assert.All(DecodedMessage.Parse(transport.Sent[^1].Data).Answers, record => Assert.Equal(0u, record.Ttl));
        Assert.Equal(watchersWhileOn - 1, network.WatcherCount);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task Toggling_zeroconf_in_the_settings_starts_and_withdraws_the_advertisement()
    {
        WriteSettings("Study-PC", zeroconf: true);
        await using var agent = CreateAgent();
        SimulatedServiceAdvertiser recorder = RecorderOf(agent);
        await TestWait.UntilAsync(() => recorder.Current is not null, "the announcement");

        await SaveSettingsAsync(agent, "Study-PC", zeroconf: false);
        await TestWait.UntilAsync(() => recorder.Current is null, "the withdrawal");

        await SaveSettingsAsync(agent, "Study-PC", zeroconf: true);
        await TestWait.UntilAsync(() => recorder.Current is not null, "the announcement after switching it on");
        Assert.Equal(2, recorder.StartCount);
    }

    [Fact]
    public async Task Renaming_the_agent_announces_the_new_name_and_an_unrelated_setting_does_not_announce_again()
    {
        WriteSettings("Gaming-PC", zeroconf: true);
        await using var agent = CreateAgent();
        SimulatedServiceAdvertiser recorder = RecorderOf(agent);
        await TestWait.UntilAsync(() => recorder.Current is not null, "the announcement");

        // The log level has nothing to do with the advertisement; the rename after it is the barrier.
        await SaveSettingsAsync(agent, "Gaming-PC", zeroconf: true, logging: new LoggingSettings("debug"));
        await SaveSettingsAsync(agent, "Study-PC", zeroconf: true, logging: new LoggingSettings("debug"));

        await TestWait.UntilAsync(() => recorder.Current?.InstanceName == "Study-PC", "the announcement of the new name");
        Assert.Equal("Study-PC", recorder.Current!.TxtRecords["name"]);
        Assert.Equal(2, recorder.StartCount);
    }

    [Fact]
    public async Task A_resume_announces_again_and_a_sleep_does_not()
    {
        WriteSettings("Study-PC", zeroconf: true);
        await using var agent = CreateAgent();
        SimulatedServiceAdvertiser recorder = RecorderOf(agent);
        SimulatedPowerEvents power = agent.Services.GetRequiredService<SimulatedPlatform>().Power;
        await TestWait.UntilAsync(() => recorder.Current is not null, "the announcement");

        power.Simulate(PowerEventKind.Sleep);
        await Task.Delay(300);
        Assert.Equal(1, recorder.StartCount);

        power.Simulate(PowerEventKind.Resume);

        await TestWait.UntilAsync(() => recorder.StartCount == 2, "the announcement after the resume");
    }

    [Fact]
    public async Task A_burst_of_network_changes_announces_once_after_the_network_has_been_quiet()
    {
        WriteSettings("Study-PC", zeroconf: true);
        await using var agent = CreateAgent();
        SimulatedServiceAdvertiser recorder = RecorderOf(agent);
        FakeNetworkChangeSource network = agent.Services.GetRequiredService<FakeNetworkChangeSource>();
        await TestWait.UntilAsync(() => recorder.Current is not null && network.WatcherCount >= 1, "the announcement and the network watch");

        network.Raise();
        network.Raise();
        network.Raise();

        await TestWait.UntilAsync(() => recorder.StartCount == 2, "the announcement after the network change");
        await Task.Delay(400);
        Assert.Equal(2, recorder.StartCount);
    }

    [Fact]
    public async Task Stopping_the_agent_withdraws_the_advertisement()
    {
        WriteSettings("Study-PC", zeroconf: true);
        await using var agent = CreateAgent();
        SimulatedServiceAdvertiser recorder = RecorderOf(agent);
        FakeNetworkChangeSource network = agent.Services.GetRequiredService<FakeNetworkChangeSource>();
        await TestWait.UntilAsync(() => recorder.Current is not null, "the announcement");

        await agent.DisposeAsync();

        Assert.Null(recorder.Current);
        Assert.Equal(0, network.WatcherCount);
    }

    [Fact]
    public async Task The_real_responder_sends_a_goodbye_when_the_agent_stops()
    {
        WriteSettings("Study-PC", zeroconf: true);
        var factory = new FakeMdnsTransportFactory();
        await using var agent = CreateAgent(services => UseResponderOn(services, factory));
        MdnsServiceAdvertiser advertiser = agent.Services.GetRequiredService<MdnsServiceAdvertiser>();
        await TestWait.UntilAsync(() => advertiser.IsAdvertising, "the announcement");
        FakeMdnsTransport transport = factory.Last!;

        await agent.DisposeAsync();

        Assert.True(transport.IsDisposed);
        DecodedMessage goodbye = DecodedMessage.Parse(transport.Sent[^1].Data);
        Assert.All(goodbye.Answers, record => Assert.Equal(0u, record.Ttl));
    }
}
