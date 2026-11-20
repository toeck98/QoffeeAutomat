using System.Net.WebSockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Agent.Hub.ConfigSync;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Link;

/// <summary>
/// The WebSocket endpoint <c>/link/v1</c> of QoffeeLink (PLAN.md S07). Before the upgrade the request must use
/// HTTPS and carry <c>Authorization: Bearer &lt;link token&gt;</c> of a paired client; otherwise it is answered
/// with an HTTP error (401 for a missing or unknown token) and the connection is never upgraded. The upgrade
/// negotiates the sub-protocol <c>qoffeelink.v1</c> and <c>permessage-deflate</c>.
/// </summary>
public static class LinkEndpoint
{
    private const string BearerPrefix = "Bearer ";
    private const string SubProtocolHeader = "Sec-WebSocket-Protocol";

    /// <summary>Maps <c>GET /link/v1</c>.</summary>
    /// <param name="routes">The route builder.</param>
    /// <returns>The builder of the endpoint.</returns>
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        return routes.MapGet(ProtocolInfo.LinkPath, HandleAsync);
    }

    /// <summary>Creates the options of the WebSocket middleware: the keep-alive ping of the agent and the time without traffic after which a peer is dropped.</summary>
    /// <param name="options">The link options.</param>
    /// <returns>The middleware options.</returns>
    public static WebSocketOptions CreateWebSocketOptions(LinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var webSocketOptions = new WebSocketOptions { KeepAliveInterval = options.PingInterval };

        // Newer ASP.NET Core versions drop a connection whose peer does not answer a ping within a timeout.
        // Setting the property by name keeps the agent building and working on versions that do not have it;
        // there the peer's own pings and the TCP stack still end dead connections, only later than S07 asks.
        PropertyInfo? timeout = typeof(WebSocketOptions).GetProperty("KeepAliveTimeout", BindingFlags.Public | BindingFlags.Instance);
        if (timeout is { CanWrite: true } && timeout.PropertyType == typeof(TimeSpan))
        {
            timeout.SetValue(webSocketOptions, options.IdleTimeout - options.PingInterval);
        }

        return webSocketOptions;
    }

    /// <summary>Reads the token of an <c>Authorization: Bearer</c> header.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The token, or null when the header is missing or is not a bearer header.</returns>
    public static string? ReadBearerToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string header = request.Headers["Authorization"].ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string token = header[BearerPrefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static async Task HandleAsync(
        HttpContext context,
        LinkServer server,
        PairingService pairing,
        IHubPolicy policy,
        ILogger<LinkServer> logger)
    {
        if (!context.Request.IsHttps)
        {
            await ApiResults.Problem(StatusCodes.Status403Forbidden, "https_required", "QoffeeLink is only available over HTTPS.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (policy.DirectLinkDenied)
        {
            // The hub manages this PC and does not allow direct links (PLAN.md S20, D-029); the clients that are connected were closed when the policy took effect.
            LinkLog.Refused(logger, "direct links are denied by the hub policy");
            await ApiResults.Problem(StatusCodes.Status403Forbidden, HubConfigProblemCodes.DirectLinkDenied, "The hub that manages this PC does not allow direct links.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        LinkClient? client = await pairing.AuthenticateAsync(ReadBearerToken(context.Request), context.RequestAborted).ConfigureAwait(false);
        if (client is null)
        {
            LinkLog.Refused(logger, "missing or unknown link token");
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            await ApiResults.Problem(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, "The link token is missing, unknown or revoked.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (server.IsStopping)
        {
            await ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "stopping", "The agent is stopping.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await ApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "This endpoint needs a WebSocket upgrade request.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!OffersSubProtocol(context.Request))
        {
            await ApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "The WebSocket sub-protocol " + ProtocolInfo.SubProtocol + " is required.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // Compression is safe here: no frame of the agent contains a credential (S07).
        var accept = new WebSocketAcceptContext
        {
            SubProtocol = ProtocolInfo.SubProtocol,
            DangerousEnableCompression = true,
        };
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(accept).ConfigureAwait(false);
        await server.ServeAsync(socket, client, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool OffersSubProtocol(HttpRequest request)
    {
        foreach (string? line in request.Headers[SubProtocolHeader])
        {
            if (line is null)
            {
                continue;
            }

            foreach (string offered in line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(offered, ProtocolInfo.SubProtocol, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
