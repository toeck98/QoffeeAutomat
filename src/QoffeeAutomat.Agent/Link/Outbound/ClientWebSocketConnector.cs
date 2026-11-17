using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Link.Outbound;

/// <summary>Opens the WebSocket of an outbound link. Tests replace it with a connector that returns an in-memory socket.</summary>
public interface IOutboundWebSocketConnector
{
    /// <summary>
    /// Dials the endpoint: TLS, the upgrade request with the bearer credential and the sub-protocol <c>qoffeelink.v1</c>.
    /// The returned socket is open and has the negotiated sub-protocol; the caller owns it.
    /// </summary>
    /// <param name="endpoint">Where to dial.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>The open WebSocket.</returns>
    /// <exception cref="OutboundDialException">The attempt failed; <see cref="OutboundDialException.Failure"/> says how.</exception>
    Task<WebSocket> ConnectAsync(OutboundEndpoint endpoint, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IOutboundWebSocketConnector"/> on <see cref="ClientWebSocket"/> (ADR 0003). The URL must follow
/// <see cref="OutboundLinkRules"/> (wss, or ws for a loopback host). Without a fingerprint the certificate of the peer is
/// validated by the operating system; with one, the peer's certificate must be exactly that one, and nothing else is accepted,
/// whatever the chain says. The credential is only put into the <c>Authorization</c> header of the upgrade request.
/// </summary>
public sealed class ClientWebSocketConnector : IOutboundWebSocketConnector
{
    private readonly OutboundOptions _options;

    /// <summary>Creates the connector.</summary>
    /// <param name="options">Timings.</param>
    public ClientWebSocketConnector(OutboundOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
    }

    /// <inheritdoc />
    public async Task<WebSocket> ConnectAsync(OutboundEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (OutboundLinkRules.CheckUrl(endpoint.Url) is { } urlProblem)
        {
            throw new OutboundDialException(OutboundFailure.Protocol, urlProblem);
        }

        var socket = new ClientWebSocket();
        bool connected = false;
        try
        {
            Configure(socket.Options, endpoint);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);
            await socket.ConnectAsync(endpoint.Url, timeout.Token).ConfigureAwait(false);

            if (!string.Equals(socket.SubProtocol, ProtocolInfo.SubProtocol, StringComparison.Ordinal))
            {
                throw new OutboundDialException(OutboundFailure.Protocol, "The peer did not select the sub-protocol " + ProtocolInfo.SubProtocol + ".");
            }

            connected = true;
            return socket;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OutboundDialException(
                OutboundFailure.Unreachable,
                string.Create(CultureInfo.InvariantCulture, $"The peer did not answer within {_options.ConnectTimeout.TotalSeconds:0} seconds."));
        }
        catch (WebSocketException exception)
        {
            throw Classify(socket, exception);
        }
        catch (HttpRequestException exception)
        {
            throw Classify(exception);
        }
        finally
        {
            if (!connected)
            {
                socket.Dispose();
            }
        }
    }

    private static OutboundDialException Classify(ClientWebSocket socket, WebSocketException exception)
    {
        int status = (int)socket.HttpStatusCode;
        if (status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            return new OutboundDialException(
                OutboundFailure.Unauthorized,
                string.Create(CultureInfo.InvariantCulture, $"The peer rejected the credential (HTTP {status})."),
                exception);
        }

        if (status != 0)
        {
            return new OutboundDialException(
                OutboundFailure.Refused,
                string.Create(CultureInfo.InvariantCulture, $"The peer refused the connection (HTTP {status})."),
                exception);
        }

        return exception.InnerException is HttpRequestException inner
            ? Classify(inner)
            : new OutboundDialException(OutboundFailure.Unreachable, "The WebSocket connection could not be opened: " + exception.Message, exception);
    }

    private static OutboundDialException Classify(HttpRequestException exception)
    {
        return exception.InnerException is AuthenticationException
            ? new OutboundDialException(OutboundFailure.Certificate, "The certificate of the peer is not acceptable.", exception)
            : new OutboundDialException(OutboundFailure.Unreachable, "The peer cannot be reached: " + exception.Message, exception);
    }

    private void Configure(ClientWebSocketOptions options, OutboundEndpoint endpoint)
    {
        options.AddSubProtocol(ProtocolInfo.SubProtocol);
        options.SetRequestHeader("Authorization", "Bearer " + endpoint.Credential);
        options.KeepAliveInterval = _options.PingInterval;
        options.CollectHttpResponseDetails = true;

        // permessage-deflate is safe here: no frame contains a credential (S07).
        options.DangerousDeflateOptions = new WebSocketDeflateOptions();

        // A peer that does not answer a ping within the rest of the idle time is dropped (S07); the property exists on newer runtimes.
        PropertyInfo? keepAliveTimeout = typeof(ClientWebSocketOptions).GetProperty("KeepAliveTimeout", BindingFlags.Public | BindingFlags.Instance);
        if (keepAliveTimeout is { CanWrite: true } && keepAliveTimeout.PropertyType == typeof(TimeSpan))
        {
            keepAliveTimeout.SetValue(options, _options.IdleTimeout - _options.PingInterval);
        }

        byte[]? pinned = endpoint.Fingerprint is { } fingerprint ? Convert.FromHexString(fingerprint) : null;
        options.RemoteCertificateValidationCallback = (_, certificate, _, errors) => CertificateIsAcceptable(pinned, certificate, errors);
    }

    /// <summary>The certificate decision: the pinned certificate, or a certificate that the system trusts.</summary>
    /// <param name="pinned">The SHA-256 hash of the certificate to accept, or null for normal validation.</param>
    /// <param name="certificate">The certificate of the peer.</param>
    /// <param name="errors">The result of the system validation.</param>
    /// <returns>True when the connection may continue.</returns>
    public static bool CertificateIsAcceptable(byte[]? pinned, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (pinned is null)
        {
            return errors == SslPolicyErrors.None;
        }

        if (certificate is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), pinned);
    }
}
