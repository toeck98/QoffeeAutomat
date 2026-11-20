using System.Net;
using QoffeeAutomat.Hub.Api;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>
/// Restricts the Home Assistant ingress listener to the Supervisor address (PLAN.md S20, T918). Runs before
/// <see cref="TrustedProxyMiddleware"/> so <c>X-Forwarded-For</c> cannot spoof the peer address.
/// </summary>
public sealed class IngressGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HubSettings _settings;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="settings">The resolved hub settings.</param>
    public IngressGateMiddleware(RequestDelegate next, HubSettings settings)
    {
        _next = next;
        _settings = settings;
    }

    /// <summary>Rejects connections to the ingress listener that do not come from the configured Supervisor address.</summary>
    /// <param name="context">The HTTP request context.</param>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsIngressRequest(context, _settings)
            && !IsAllowedPeer(context.Connection.RemoteIpAddress, _settings.IngressAllowedAddress))
        {
            return HubApiResults.Problem(
                StatusCodes.Status403Forbidden,
                "ingress_forbidden",
                "The ingress listener accepts connections from the Home Assistant Supervisor only.")
                .ExecuteAsync(context);
        }

        return _next(context);
    }

    /// <summary>Returns true when the request arrived on the configured ingress listener port.</summary>
    /// <param name="context">The HTTP request context.</param>
    /// <param name="settings">The resolved hub settings.</param>
    public static bool IsIngressRequest(HttpContext context, HubSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        return settings.IngressPort is { } port && context.Connection.LocalPort == port;
    }

    /// <summary>Returns true when the peer IP address matches the allowed ingress address (normalizing IPv4-mapped IPv6).</summary>
    /// <param name="remoteIp">The remote peer IP address.</param>
    /// <param name="allowedAddress">The configured allowed address.</param>
    public static bool IsAllowedPeer(IPAddress? remoteIp, IPAddress allowedAddress)
    {
        ArgumentNullException.ThrowIfNull(allowedAddress);

        if (remoteIp is null)
        {
            return false;
        }

        IPAddress normalizedRemote = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        IPAddress normalizedAllowed = allowedAddress.IsIPv4MappedToIPv6 ? allowedAddress.MapToIPv4() : allowedAddress;
        return normalizedRemote.Equals(normalizedAllowed);
    }
}
