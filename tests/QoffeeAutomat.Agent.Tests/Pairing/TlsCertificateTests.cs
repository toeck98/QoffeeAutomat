using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Security.Tls;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Security;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Pairing;

/// <summary>The self-signed certificate of the link listener (T211, PLAN.md S09 item 3).</summary>
[Trait("Category", "Pairing")]
public sealed partial class TlsCertificateTests : IDisposable
{
    private readonly SimulatedClock _clock = new();
    private readonly TestSecrets _secrets = new();

    public void Dispose() => _secrets.Dispose();

    [GeneratedRegex("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();

    [Fact]
    public async Task The_certificate_is_created_on_first_use_and_stored_in_the_secret_store()
    {
        SecretStore store = _secrets.Open();
        using var provider = new TlsCertificateProvider(store, _clock, () => "pc-test");

        X509Certificate2 certificate = provider.GetOrCreate();

        Assert.True(provider.CreatedNewCertificate);
        Assert.True(certificate.HasPrivateKey);
        Assert.Contains(SecretNames.TlsCertificate, await store.ListNamesAsync(CancellationToken.None));
    }

    [Fact]
    public void The_certificate_is_ecdsa_p256_with_the_host_names()
    {
        using var provider = new TlsCertificateProvider(_secrets.Open(), _clock, () => "PC_Test");

        X509Certificate2 certificate = provider.GetOrCreate();

        using ECDsa key = certificate.GetECDsaPublicKey()!;
        Assert.Equal(256, key.KeySize);
        Assert.Equal("CN=pc-test", certificate.Subject);

        string names = certificate.Extensions["2.5.29.17"]!.Format(multiLine: false);
        Assert.Contains("pc-test", names, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pc-test.local", names, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("localhost", names, StringComparison.OrdinalIgnoreCase);
        Assert.True(certificate.NotAfter.ToUniversalTime() > _clock.GetUtcNow().UtcDateTime.AddYears(9));
    }

    [Fact]
    public void The_fingerprint_is_the_sha256_of_the_certificate_in_colon_notation()
    {
        using var provider = new TlsCertificateProvider(_secrets.Open(), _clock, () => "pc-test");

        string? fingerprint = provider.Fingerprint;

        Assert.NotNull(fingerprint);
        Assert.Matches(FingerprintPattern(), fingerprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(provider.GetOrCreate().RawData)), fingerprint.Replace(":", string.Empty, StringComparison.Ordinal));
        Assert.True(CertificateFingerprint.AreEqual(fingerprint, fingerprint.ToLowerInvariant().Replace(':', ' ')));
        Assert.False(CertificateFingerprint.AreEqual(fingerprint, null));
    }

    [Fact]
    public void The_certificate_survives_restarts_and_its_fingerprint_stays_the_same()
    {
        string? first;
        using (var provider = new TlsCertificateProvider(_secrets.Open(), _clock, () => "pc-test"))
        {
            first = provider.Fingerprint;
            Assert.True(provider.CreatedNewCertificate);
        }

        // Another start of the agent: new objects over the same files, a different host name even.
        using var restarted = new TlsCertificateProvider(_secrets.Open(), _clock, () => "renamed-pc");

        Assert.Equal(first, restarted.Fingerprint);
        Assert.False(restarted.CreatedNewCertificate);
        Assert.True(restarted.GetOrCreate().HasPrivateKey);
    }

    [Fact]
    public void An_expired_certificate_is_replaced()
    {
        string? first;
        using (var provider = new TlsCertificateProvider(_secrets.Open(), _clock, () => "pc-test"))
        {
            first = provider.Fingerprint;
        }

        _clock.Advance(SelfSignedCertificateFactory.DefaultValidity + TimeSpan.FromDays(2));
        using var later = new TlsCertificateProvider(_secrets.Open(), _clock, () => "pc-test");

        Assert.NotEqual(first, later.Fingerprint);
        Assert.True(later.CreatedNewCertificate);
    }

    [Fact]
    public async Task An_unreadable_certificate_is_replaced()
    {
        SecretStore store = _secrets.Open();
        byte[] garbage = [1, 2, 3, 4];
        await store.SetBytesAsync(SecretNames.TlsCertificate, garbage, CancellationToken.None);
        using var provider = new TlsCertificateProvider(store, _clock, () => "pc-test");

        X509Certificate2 certificate = provider.GetOrCreate();

        Assert.True(certificate.HasPrivateKey);
        Assert.True(provider.CreatedNewCertificate);
    }

    [Fact]
    public async Task The_https_listener_serves_the_stored_certificate_across_restarts()
    {
        using var data = new TempDataDirectory();
        var options = new AgentOptions(Platform: "simulated", DataDir: data.Path, UiPort: 0, LinkPort: 0);

        string first = await ServedFingerprintAsync(options);
        string second = await ServedFingerprintAsync(options);

        Assert.Equal(first, second);
        Assert.Matches(FingerprintPattern(), first);
    }

    /// <summary>Starts the agent, connects to its HTTPS listener while pinning the expected fingerprint, and stops it.</summary>
    private static async Task<string> ServedFingerprintAsync(AgentOptions options)
    {
        WebApplication app = AgentHost.Build(options);
        await app.StartAsync();
        try
        {
            string expected = app.Services.GetRequiredService<ITlsCertificateSource>().Fingerprint!;
            IServerAddressesFeature addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            string https = Assert.Single(addresses.Addresses, address => address.StartsWith("https://", StringComparison.Ordinal));
            int port = new Uri(https).Port;

            // The client trusts the certificate only when its fingerprint is the expected one.
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && CertificateFingerprint.AreEqual(CertificateFingerprint.Compute(certificate), expected),
            };
            using var client = new HttpClient(handler);
            using HttpResponseMessage response = await client.GetAsync(
                new Uri("https://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/healthz"));

            Assert.True(response.IsSuccessStatusCode);
            return expected;
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
