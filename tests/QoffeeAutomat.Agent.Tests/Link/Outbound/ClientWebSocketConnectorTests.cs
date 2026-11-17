using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QoffeeAutomat.Agent.Link.Outbound;

namespace QoffeeAutomat.Agent.Tests.Link.Outbound;

/// <summary>The TLS rules of the real dial-out connector (ADR 0003): a pinned certificate is the only one accepted, otherwise the system validation decides.</summary>
[Trait("Category", "OutboundLink")]
public sealed class ClientWebSocketConnectorTests
{
    [Fact]
    public void A_pinned_certificate_is_accepted_even_though_the_system_does_not_trust_it()
    {
        using X509Certificate2 certificate = CreateCertificate("CN=pinned");

        bool accepted = ClientWebSocketConnector.CertificateIsAcceptable(
            SHA256.HashData(certificate.RawData),
            certificate,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.True(accepted);
    }

    [Fact]
    public void Another_certificate_is_refused_even_though_the_system_trusts_it()
    {
        using X509Certificate2 pinned = CreateCertificate("CN=pinned");
        using X509Certificate2 other = CreateCertificate("CN=other");

        bool accepted = ClientWebSocketConnector.CertificateIsAcceptable(SHA256.HashData(pinned.RawData), other, SslPolicyErrors.None);

        Assert.False(accepted);
    }

    [Fact]
    public void A_missing_certificate_is_refused_when_one_is_pinned()
    {
        using X509Certificate2 pinned = CreateCertificate("CN=pinned");

        Assert.False(ClientWebSocketConnector.CertificateIsAcceptable(SHA256.HashData(pinned.RawData), null, SslPolicyErrors.None));
    }

    [Theory]
    [InlineData(SslPolicyErrors.None, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, false)]
    public void Without_a_pin_the_system_validation_decides(SslPolicyErrors errors, bool expected)
    {
        using X509Certificate2 certificate = CreateCertificate("CN=any");

        Assert.Equal(expected, ClientWebSocketConnector.CertificateIsAcceptable(null, certificate, errors));
    }

    [Fact]
    public async Task A_url_that_breaks_the_rules_is_refused_before_anything_is_sent()
    {
        var connector = new ClientWebSocketConnector(new OutboundOptions());
        var endpoint = new OutboundEndpoint(new Uri("ws://ha.example.org/api/qoffeeautomat/reverse/v1"), "qalr_token");

        OutboundDialException failure = await Assert.ThrowsAsync<OutboundDialException>(() => connector.ConnectAsync(endpoint, CancellationToken.None));

        Assert.Equal(OutboundFailure.Protocol, failure.Failure);
        Assert.DoesNotContain("qalr_token", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_peer_that_cannot_be_reached_is_reported_as_unreachable()
    {
        var connector = new ClientWebSocketConnector(new OutboundOptions { ConnectTimeout = TimeSpan.FromSeconds(5) });

        // Port 1 on the loopback interface has no listener, so the connection is refused at once.
        var endpoint = new OutboundEndpoint(new Uri("ws://127.0.0.1:1/api/qoffeeautomat/reverse/v1"), "qalr_token");

        OutboundDialException failure = await Assert.ThrowsAsync<OutboundDialException>(() => connector.ConnectAsync(endpoint, CancellationToken.None));

        Assert.Equal(OutboundFailure.Unreachable, failure.Failure);
        Assert.DoesNotContain("qalr_token", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_attempt_is_not_a_failure_of_the_peer()
    {
        var connector = new ClientWebSocketConnector(new OutboundOptions());
        var endpoint = new OutboundEndpoint(new Uri("ws://127.0.0.1:1/api/qoffeeautomat/reverse/v1"), "qalr_token");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.ConnectAsync(endpoint, cancelled.Token));
    }

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
