using System.Globalization;

namespace QoffeeAutomat.Agent.Link.Outbound;

/// <summary>
/// Where an outbound link dials and how it proves who it is (ADR 0003): a WebSocket URL, the bearer credential for the upgrade
/// request and, optionally, the fingerprint of the one certificate that the peer may present. The reverse link to Home Assistant and
/// the hub link both describe their peer like this.
/// </summary>
/// <param name="Url">The <c>wss://</c> URL (see <c>OutboundLinkRules</c>).</param>
/// <param name="Credential">The bearer credential; sent only in the <c>Authorization</c> header of the upgrade request, never in a frame.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint of the certificate to accept, 64 upper-case hexadecimal digits; null means the normal certificate validation.</param>
public sealed record OutboundEndpoint(Uri Url, string Credential, string? Fingerprint = null)
{
    /// <summary>The URL without a query string, for logs and messages; it never contains the credential.</summary>
    public string DisplayUrl => Url.GetLeftPart(UriPartial.Path);

    /// <summary>Describes the endpoint without the credential, so that a log line or a failed assertion cannot leak it.</summary>
    /// <returns>The description.</returns>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"OutboundEndpoint {{ Url = {DisplayUrl}, Pinned = {Fingerprint is not null} }}");
    }
}
