using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>
/// Opens the listener of the hub (PLAN.md S20, "Listeners"): <c>https://0.0.0.0:8490</c> with the self-signed certificate, with the
/// operator's certificate, or plain HTTP behind a trusted reverse proxy. TLS 1.2 and 1.3 only.
/// </summary>
public sealed class HubListenerSetup : IConfigureOptions<KestrelServerOptions>
{
    private readonly HubSettings _settings;
    private readonly HubCertificateProvider _certificates;

    /// <summary>Creates the setup.</summary>
    /// <param name="settings">The listener settings.</param>
    /// <param name="certificates">The source of the TLS certificate.</param>
    public HubListenerSetup(HubSettings settings, HubCertificateProvider certificates)
    {
        _settings = settings;
        _certificates = certificates;
    }

    /// <inheritdoc />
    public void Configure(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        X509Certificate2? certificate = _certificates.TryGetCertificate();

        options.AddServerHeader = false;
        options.Listen(_settings.Bind, _settings.Port, listen =>
        {
            if (certificate is null)
            {
                return;
            }

            listen.UseHttps(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = certificate,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
        });

        if (_settings.IngressPort is { } ingressPort)
        {
            // Home Assistant Supervisor terminates TLS and proxies plain HTTP to the ingress port (PLAN.md S20).
            options.Listen(_settings.Bind, ingressPort);
        }
    }
}
