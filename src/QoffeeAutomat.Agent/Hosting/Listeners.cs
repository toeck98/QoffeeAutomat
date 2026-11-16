using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>One Kestrel listener.</summary>
/// <param name="Address">The local address to bind.</param>
/// <param name="Port">The port; zero picks a free one.</param>
/// <param name="UseHttps">Whether the listener speaks TLS.</param>
public sealed record ListenerSpec(IPAddress Address, int Port, bool UseHttps);

/// <summary>
/// The two listeners of PLAN.md D-018: <c>http://127.0.0.1:&lt;uiPort&gt;</c> for the UI and API, bound
/// to loopback only, and <c>https://0.0.0.0:&lt;linkPort&gt;</c> for QoffeeLink and pairing. The HTTPS
/// listener exists only once a certificate is available (T211); until then the agent is reachable
/// from this PC only.
/// </summary>
public static class ListenerPlan
{
    /// <summary>Lists the listeners to open.</summary>
    /// <param name="uiPort">The port of the loopback listener.</param>
    /// <param name="linkPort">The port of the HTTPS listener.</param>
    /// <param name="hasCertificate">Whether a TLS certificate exists.</param>
    /// <returns>The listeners; the loopback listener is always first.</returns>
    public static IReadOnlyList<ListenerSpec> Create(int uiPort, int linkPort, bool hasCertificate)
    {
        var listeners = new List<ListenerSpec> { new(IPAddress.Loopback, uiPort, UseHttps: false) };
        if (hasCertificate)
        {
            listeners.Add(new ListenerSpec(IPAddress.Any, linkPort, UseHttps: true));
        }

        return listeners;
    }
}

/// <summary>
/// Supplies the TLS certificate of the link listener. The agent uses
/// <see cref="Security.Tls.TlsCertificateProvider"/> (T211); <see cref="NoCertificateSource"/> keeps the
/// agent on loopback only and is for tests.
/// </summary>
public interface ITlsCertificateSource
{
    /// <summary>The SHA-256 fingerprint of the certificate (hex pairs separated by colons), or null when there is no certificate.</summary>
    string? Fingerprint { get; }

    /// <summary>Returns the certificate with its private key, or null when none exists.</summary>
    /// <returns>The certificate, or null.</returns>
    X509Certificate2? TryGetCertificate();
}

/// <summary>An <see cref="ITlsCertificateSource"/> without a certificate: only the loopback listener opens.</summary>
public sealed class NoCertificateSource : ITlsCertificateSource
{
    /// <inheritdoc />
    public string? Fingerprint => null;

    /// <inheritdoc />
    public X509Certificate2? TryGetCertificate() => null;
}

/// <summary>Opens the listeners of <see cref="ListenerPlan"/> on Kestrel.</summary>
public sealed class KestrelListenerSetup : IConfigureOptions<KestrelServerOptions>
{
    private readonly IAppPaths _paths;
    private readonly AgentOptions _options;
    private readonly ITlsCertificateSource _certificates;

    /// <summary>Creates the setup.</summary>
    /// <param name="paths">Where the settings are.</param>
    /// <param name="options">The command-line options; their ports override the settings.</param>
    /// <param name="certificates">The source of the TLS certificate.</param>
    public KestrelListenerSetup(IAppPaths paths, AgentOptions options, ITlsCertificateSource certificates)
    {
        _paths = paths;
        _options = options;
        _certificates = certificates;
    }

    /// <inheritdoc />
    public void Configure(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = AgentSettingsLoader.LoadOrDefault(_paths, out _);
        int uiPort = _options.UiPort ?? settings.EffectiveUiPort;
        int linkPort = _options.LinkPort ?? settings.EffectiveLinkPort;
        X509Certificate2? certificate = _certificates.TryGetCertificate();

        foreach (ListenerSpec listener in ListenerPlan.Create(uiPort, linkPort, certificate is not null))
        {
            if (listener.UseHttps)
            {
                options.Listen(listener.Address, listener.Port, listenOptions => listenOptions.UseHttps(certificate!));
            }
            else
            {
                options.Listen(listener.Address, listener.Port);
            }
        }
    }
}
