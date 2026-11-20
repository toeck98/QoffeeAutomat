using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Agent.Hub;

/// <summary>
/// The WebSocket connector of the hub link (PLAN.md S20, T905). It dials like every outbound link (<see cref="OutboundConnector"/> with the connector
/// of T710) but understands the answers of the hub to a refused upgrade, which the WebSocket client does not show: a <c>ClientWebSocket</c> reports only
/// the HTTP status of a failed upgrade, not its body. When the hub answers 401 or 403, this connector asks the same address once more with a plain
/// <c>GET</c> and reads the error code from the body: <c>revoked</c> (the credential was revoked; <see cref="OutboundFailure.Unauthorized"/>, which ends
/// the dialling) or anything else, such as <c>pending_approval</c> or <c>unauthorized</c> (<see cref="OutboundFailure.Refused"/>: the agent keeps its
/// credential and tries again after the delay, because only a revocation may delete it). The hub checks the credential before it looks at the upgrade,
/// so the plain request gets the same answer.
/// </summary>
public sealed class HubWebSocketConnector : IOutboundWebSocketConnector
{
    /// <summary>The error code of an agent link that is refused until an administrator accepts the agent (HTTP 403).</summary>
    public const string PendingApprovalCode = "pending_approval";

    private const int MaxResponseBytes = 64 * 1024;

    private readonly IOutboundWebSocketConnector _inner;
    private readonly IHubHttpClientFactory _http;
    private readonly Func<CancellationToken, Task> _onRevoked;

    /// <summary>Creates the connector.</summary>
    /// <param name="inner">Opens the WebSocket.</param>
    /// <param name="http">Creates the HTTP clients for the probe.</param>
    /// <param name="onRevoked">Called when the hub says that the credential is revoked, before the dial fails; it deletes the credential.</param>
    public HubWebSocketConnector(IOutboundWebSocketConnector inner, IHubHttpClientFactory http, Func<CancellationToken, Task> onRevoked)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(onRevoked);

        _inner = inner;
        _http = http;
        _onRevoked = onRevoked;
    }

    /// <inheritdoc />
    public async Task<WebSocket> ConnectAsync(OutboundEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        try
        {
            return await _inner.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OutboundDialException exception) when (exception.Failure == OutboundFailure.Unauthorized)
        {
            string? code = await ReadRefusalCodeAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (string.Equals(code, HubErrorCodes.Revoked, StringComparison.Ordinal))
            {
                await _onRevoked(cancellationToken).ConfigureAwait(false);
                throw new OutboundDialException(OutboundFailure.Unauthorized, "The hub revoked the credential of this agent.", exception);
            }

            string reason = string.Equals(code, PendingApprovalCode, StringComparison.Ordinal)
                ? "The hub waits for an administrator to approve this agent."
                : "The hub did not accept the credential; the agent keeps it and tries again.";
            throw new OutboundDialException(OutboundFailure.Refused, reason, exception);
        }
    }

    /// <summary>Asks the hub why it refuses the credential of the endpoint.</summary>
    /// <param name="endpoint">The endpoint that was dialled.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The error code of the answer (<c>revoked</c>, <c>pending_approval</c>, <c>unauthorized</c>), or null when the hub answered otherwise or cannot be reached.</returns>
    public async Task<string?> ReadRefusalCodeAsync(OutboundEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var address = new UriBuilder(endpoint.Url)
        {
            Scheme = string.Equals(endpoint.Url.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase) ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Credential);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using HttpClient client = _http.Create(endpoint.Fingerprint);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                return null;
            }

            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            return HubHttp.ReadErrorCode(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
