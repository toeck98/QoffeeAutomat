using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Hub.ConfigSync;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Discovery;

/// <summary>The names and values of the zeroconf advertisement (PLAN.md D-016, S07).</summary>
public static class ZeroconfNames
{
    /// <summary>The DNS-SD service type of QoffeeAutomat.</summary>
    public const string ServiceType = "_qoffeeautomat._tcp.local.";

    /// <summary>TXT key: the persistent agent id.</summary>
    public const string IdKey = "id";

    /// <summary>TXT key: the agent name.</summary>
    public const string NameKey = "name";

    /// <summary>TXT key: the agent version.</summary>
    public const string VersionKey = "v";

    /// <summary>TXT key: the range of QoffeeLink protocol versions, <c>min-max</c>.</summary>
    public const string ProtocolKey = "pv";

    private const string DefaultInstance = "QoffeeAutomat";
    private const string DefaultHost = "qoffeeautomat";

    /// <summary>The value of the TXT record <c>pv</c>: <c>min-max</c> of the supported protocol versions.</summary>
    public static string ProtocolRange { get; } = string.Create(CultureInfo.InvariantCulture, $"{ProtocolInfo.Version}-{ProtocolInfo.Version}");

    /// <summary>Cuts a text to at most <paramref name="maxBytes"/> bytes of UTF-8 without splitting a character.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxBytes">The most bytes.</param>
    /// <returns>The text, or its longest prefix that fits.</returns>
    public static string TruncateUtf8(string text, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var builder = new StringBuilder();
        int bytes = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            bytes += size;
        }

        return builder.ToString();
    }

    /// <summary>Makes the DNS label of the service instance from the agent name: at most 63 bytes, no control characters.</summary>
    /// <param name="name">The agent name.</param>
    /// <returns>The label; <c>QoffeeAutomat</c> when the name has no usable characters.</returns>
    public static string ToInstanceLabel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        string cleaned = string.Concat(name.Select(character => char.IsControl(character) ? ' ' : character)).Trim();
        return cleaned.Length == 0 ? DefaultInstance : TruncateUtf8(cleaned, 63);
    }

    /// <summary>Makes the host label of the address records (<c>&lt;label&gt;.local.</c>) from the host name: lower case letters, digits and hyphens.</summary>
    /// <param name="hostname">The host name, possibly with a domain.</param>
    /// <returns>The label; <c>qoffeeautomat</c> when the host name has no usable characters.</returns>
    public static string ToHostLabel(string hostname)
    {
        ArgumentNullException.ThrowIfNull(hostname);

        int dot = hostname.IndexOf('.', StringComparison.Ordinal);
        string first = dot < 0 ? hostname : hostname[..dot];
        string mapped = string.Concat(first.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')).Trim('-');
        if (mapped.Length == 0)
        {
            return DefaultHost;
        }

        return mapped.Length > 63 ? mapped[..63].TrimEnd('-') : mapped;
    }
}

/// <summary>Timing of <see cref="ZeroconfService"/>.</summary>
public sealed class ZeroconfOptions
{
    /// <summary>How long the network must be quiet after a change before the agent is announced again; one change usually raises several events.</summary>
    public TimeSpan NetworkDebounce { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Decides when the agent is announced on the network (PLAN.md S07, D-016, T216) and drives the
/// <see cref="IServiceAdvertiser"/>. The announcement starts as soon as the host reports that the listeners
/// accept connections (the Home Assistant integration uses it to reconnect at once), is repeated after the computer
/// resumed, after the network changed (a few seconds after the last change) and after the agent was renamed, and is
/// withdrawn when the agent stops. The setting <c>zeroconf</c> switches it off, and then the advertiser is never
/// started, so no multicast socket is opened.
/// </summary>
public sealed class ZeroconfService : IHostedService, IAsyncDisposable
{
    private readonly ZeroconfOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServiceAdvertiser _advertiser;
    private readonly AgentIdentity _identity;
    private readonly ITlsCertificateSource _certificate;
    private readonly IPowerEvents _power;
    private readonly INetworkChangeSource _network;
    private readonly IConfigStore _config;
    private readonly IServer _server;
    private readonly TimeProvider _time;
    private readonly ILogger<ZeroconfService> _logger;
    private readonly IHubPolicy? _hubPolicy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _debounce;
    private IDisposable? _networkWatch;
    private ConfigSubscription? _settings;
    private Task _settingsPump = Task.CompletedTask;
    private CancellationTokenRegistration _startedRegistration;
    private ServiceAdvertisement? _last;
    private volatile bool _listening;
    private bool _stopped;
    private bool _disabledLogged;

    /// <summary>Creates the service.</summary>
    /// <param name="lifetime">The application lifetime; <c>ApplicationStarted</c> means the listeners accept connections.</param>
    /// <param name="advertiser">Announces the service.</param>
    /// <param name="identity">The id, name and port of the agent and the zeroconf setting.</param>
    /// <param name="certificate">The TLS certificate source; without a certificate there is no link listener to announce.</param>
    /// <param name="power">The power events of the platform.</param>
    /// <param name="network">The source of network changes.</param>
    /// <param name="config">The configuration, to follow the settings.</param>
    /// <param name="server">The web server, for the port that the link listener really got.</param>
    /// <param name="time">The clock for the debounce of network changes.</param>
    /// <param name="options">The timing.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="hubPolicy">The policy of the hub; while it denies direct links the agent is not advertised (PLAN.md S20, T908). Null means no hub policy.</param>
    public ZeroconfService(
        IHostApplicationLifetime lifetime,
        IServiceAdvertiser advertiser,
        AgentIdentity identity,
        ITlsCertificateSource certificate,
        IPowerEvents power,
        INetworkChangeSource network,
        IConfigStore config,
        IServer server,
        TimeProvider time,
        ZeroconfOptions options,
        ILogger<ZeroconfService> logger,
        IHubPolicy? hubPolicy = null)
    {
        _hubPolicy = hubPolicy;
        _options = options;
        _lifetime = lifetime;
        _advertiser = advertiser;
        _identity = identity;
        _certificate = certificate;
        _power = power;
        _network = network;
        _config = config;
        _server = server;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _debounce = _time.CreateTimer(static state => ((ZeroconfService)state!).OnNetworkQuiet(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _power.PowerChanged += OnPowerChanged;
        if (_hubPolicy is not null)
        {
            _hubPolicy.Changed += OnHubPolicyChanged;
        }

        _settings = _config.Subscribe();
        ConfigSubscription settings = _settings;
        _settingsPump = Task.Run(() => PumpSettingsAsync(settings, _stop.Token), CancellationToken.None);
        _startedRegistration = _lifetime.ApplicationStarted.Register(static state => ((ZeroconfService)state!).OnStarted(), this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _power.PowerChanged -= OnPowerChanged;
        if (_hubPolicy is not null)
        {
            _hubPolicy.Changed -= OnHubPolicyChanged;
        }

        await _startedRegistration.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _stopped = true;
                StopWatchingNetwork();
                _debounce?.Dispose();
            }
            finally
            {
                _gate.Release();
            }

            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // DisposeAsync may run concurrently with shutdown in test hosts.
        }

        _settings?.Dispose();
        await _settingsPump.ConfigureAwait(false);

        // Withdraw the advertisement while the clients are still connected, so Home Assistant sees the service go.
        await _advertiser.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stop.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnStarted()
    {
        _listening = true;
        _ = ApplyAsync(force: true);
    }

    private void OnPowerChanged(object? sender, PowerEventArgs e)
    {
        if (e.Kind == PowerEventKind.Resume)
        {
            _ = ApplyAsync(force: true);
        }
    }

    private void OnHubPolicyChanged(object? sender, EventArgs e)
    {
        // A denial withdraws the advertisement at once; the end of a denial announces the agent again.
        _ = ApplyAsync(force: true);
    }

    private void OnNetworkChanged()
    {
        // Several events arrive for one change; the announcement waits until they stop.
        _debounce?.Change(_options.NetworkDebounce, Timeout.InfiniteTimeSpan);
    }

    private void OnNetworkQuiet()
    {
        _ = ApplyAsync(force: true);
    }

    private async Task PumpSettingsAsync(ConfigSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ConfigChange change in subscription.Changes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (change.Kind == ConfigKind.Settings)
                {
                    await ApplyAsync(force: false).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The service is stopping.
        }
    }

    /// <summary>Brings the advertisement in line with the settings and the state of the agent.</summary>
    /// <param name="force">Announce even when nothing about the advertisement changed (start, resume, network change).</param>
    private async Task ApplyAsync(bool force)
    {
        try
        {
            await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_stopped)
            {
                return;
            }

            if (!_listening || _certificate.Fingerprint is null)
            {
                // Nothing accepts QoffeeLink connections (yet), so there is nothing to announce.
                return;
            }

            if (!_identity.ZeroconfEnabled || _hubPolicy is { DirectLinkDenied: true })
            {
                await WithdrawAsync().ConfigureAwait(false);
                return;
            }

            _disabledLogged = false;
            ServiceAdvertisement advertisement = BuildAdvertisement();
            if (!force && _last is not null && Same(_last, advertisement))
            {
                return;
            }

            await _advertiser.StartAsync(advertisement, _stop.Token).ConfigureAwait(false);
            _last = advertisement;
            _networkWatch ??= _network.Watch(OnNetworkChanged);
        }
        catch (OperationCanceledException)
        {
            // The service is stopping.
        }
#pragma warning disable CA1031 // The agent works without the announcement; the failure is logged and the next network change tries again.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            string reason = exception.Message;
            DiscoveryLog.AnnounceFailed(_logger, reason);

            // Keep watching: the network may come back, and a new interface is a reason to try again.
            _networkWatch ??= _network.Watch(OnNetworkChanged);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WithdrawAsync()
    {
        StopWatchingNetwork();
        if (!_disabledLogged)
        {
            _disabledLogged = true;
            DiscoveryLog.Disabled(_logger);
        }

        if (_last is not null)
        {
            _last = null;
            await _advertiser.StopAsync(_stop.Token).ConfigureAwait(false);
        }
    }

    private void StopWatchingNetwork()
    {
        _networkWatch?.Dispose();
        _networkWatch = null;
    }

    private ServiceAdvertisement BuildAdvertisement()
    {
        string name = _identity.Name;
        var records = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ZeroconfNames.IdKey] = _identity.Id,
            [ZeroconfNames.NameKey] = ZeroconfNames.TruncateUtf8(name, 200),
            [ZeroconfNames.VersionKey] = AgentVersion.Current,
            [ZeroconfNames.ProtocolKey] = ZeroconfNames.ProtocolRange,
        };
        return new ServiceAdvertisement(ZeroconfNames.ServiceType, name, ResolveLinkPort(), records);
    }

    private int ResolveLinkPort()
    {
        // A port of 0 in the settings or on the command line lets the system pick one; the server knows which.
        IServerAddressesFeature? addresses = _server.Features.Get<IServerAddressesFeature>();
        if (addresses is not null)
        {
            foreach (string address in addresses.Addresses)
            {
                if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
                    && uri.Port > 0)
                {
                    return uri.Port;
                }
            }
        }

        return _identity.LinkPort;
    }

    private static bool Same(ServiceAdvertisement left, ServiceAdvertisement right)
    {
        return string.Equals(left.ServiceType, right.ServiceType, StringComparison.Ordinal)
            && string.Equals(left.InstanceName, right.InstanceName, StringComparison.Ordinal)
            && left.Port == right.Port
            && left.TxtRecords.Count == right.TxtRecords.Count
            && left.TxtRecords.All(pair => right.TxtRecords.TryGetValue(pair.Key, out string? value) && string.Equals(value, pair.Value, StringComparison.Ordinal));
    }
}

/// <summary>Registers the zeroconf advertisement.</summary>
public static class ZeroconfServices
{
    /// <summary>
    /// Adds the service that drives the advertisement. It uses whichever <see cref="IServiceAdvertiser"/> is registered,
    /// which on the simulated platform only records what would be announced; <see cref="UseMdnsAdvertiser"/> makes the
    /// agent announce itself on the network.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddZeroconf(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMdnsTransportFactory, UdpMdnsTransportFactory>();
        services.TryAddSingleton<INetworkChangeSource, SystemNetworkChangeSource>();
        services.TryAddSingleton(new ZeroconfOptions());
        services.AddHostedService<ZeroconfService>();
        return services;
    }

    /// <summary>
    /// Replaces the platform's <see cref="IServiceAdvertiser"/> with the multicast DNS responder of the agent. The
    /// executable does this; tests keep the recording advertiser of the simulated platform, so no test opens a socket.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection UseMdnsAdvertiser(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<IServiceAdvertiser>();
        services.AddSingleton<MdnsServiceAdvertiser>();
        services.AddSingleton<IServiceAdvertiser>(provider => provider.GetRequiredService<MdnsServiceAdvertiser>());
        return services;
    }
}
