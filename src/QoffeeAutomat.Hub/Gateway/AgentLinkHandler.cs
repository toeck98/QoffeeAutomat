using System.Net.WebSockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Tunnel;

namespace QoffeeAutomat.Hub.Gateway;

/// <summary>
/// Takes over <c>GET /hub/v1/agent</c> after the credential was checked (PLAN.md S20, T904, T906): upgrades the request to a WebSocket with the sub-protocol
/// <c>qoffeelink.v1</c> and hands it to the <see cref="AgentGateway"/>. Compression is not offered: <c>credential_rotate</c> carries a credential, and credentials never
/// travel in compressed frames (S07). A request that is not a WebSocket upgrade is answered with HTTP 400; while the hub stops, with 503.
/// </summary>
public sealed class AgentLinkHandler : IAgentLinkHandler
{
    private const string SubProtocolHeader = "Sec-WebSocket-Protocol";

    private readonly AgentGateway _gateway;

    /// <summary>Creates the handler.</summary>
    /// <param name="gateway">The gateway that serves the link.</param>
    public AgentLinkHandler(AgentGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        _gateway = gateway;
    }

    /// <summary>Creates the options of the WebSocket middleware: the keep-alive ping of the hub and the time without traffic after which an agent is dropped (PLAN.md S07).</summary>
    /// <param name="options">The gateway options.</param>
    /// <returns>The middleware options.</returns>
    public static WebSocketOptions CreateWebSocketOptions(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var webSocketOptions = new WebSocketOptions { KeepAliveInterval = options.PingInterval };

        // Newer ASP.NET Core versions drop a connection whose peer does not answer a ping within a timeout. Setting the property by name keeps the hub building on
        // versions that do not have it; there the pings of the agent and the TCP stack still end dead connections, only later than S07 asks.
        PropertyInfo? timeout = typeof(WebSocketOptions).GetProperty("KeepAliveTimeout", BindingFlags.Public | BindingFlags.Instance);
        if (timeout is { CanWrite: true } && timeout.PropertyType == typeof(TimeSpan))
        {
            timeout.SetValue(webSocketOptions, options.IdleTimeout - options.PingInterval);
        }

        return webSocketOptions;
    }

    /// <inheritdoc />
    public async Task HandleAsync(HttpContext context, AgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(agent);

        if (_gateway.IsStopping)
        {
            await HubLinkErrors.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "stopping", "The hub is stopping.").ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await HubLinkErrors.WriteAsync(context, StatusCodes.Status400BadRequest, "bad_request", "This endpoint needs a WebSocket upgrade request.").ConfigureAwait(false);
            return;
        }

        if (!OffersSubProtocol(context.Request))
        {
            await HubLinkErrors.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "bad_request",
                "The WebSocket sub-protocol " + ProtocolInfo.SubProtocol + " is required.").ConfigureAwait(false);
            return;
        }

        var accept = new WebSocketAcceptContext { SubProtocol = ProtocolInfo.SubProtocol };
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(accept).ConfigureAwait(false);
        await _gateway.ServeAsync(socket, agent, context.RequestAborted).ConfigureAwait(false);
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

/// <summary>Registers the agent gateway (T906).</summary>
public static class GatewayServices
{
    /// <summary>
    /// Adds the gateway (announcing <c>config_sync</c> and <c>mgmt_tunnel</c>), its event store, the endpoint handler, the request and stream channels of the tunnel and the notifier of <c>credential_rotate</c> and <c>unenroll</c>. Call it before <c>AddHubEnrollment</c>, which
    /// registers placeholders that only apply when nothing else is registered. The hub database, the hub identity and <see cref="TimeProvider"/> must be registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddHubGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new GatewayOptions { Features = [HubFeatures.ConfigSync, HubFeatures.MgmtTunnel] });
        services.TryAddSingleton<AgentEventStore>();
        services.TryAddSingleton<AgentGateway>();
        services.TryAddSingleton<IAgentGateway>(provider => provider.GetRequiredService<AgentGateway>());
        services.TryAddSingleton<IGatewayStatistics>(provider => provider.GetRequiredService<AgentGateway>());
        services.TryAddSingleton<IAgentLinkNotifier>(provider => provider.GetRequiredService<AgentGateway>());
        services.TryAddSingleton<IAgentRequestChannel>(provider => provider.GetRequiredService<AgentGateway>());
        services.TryAddSingleton<IAgentStreamChannel>(provider => provider.GetRequiredService<AgentGateway>());
        services.TryAddSingleton<IAgentLinkHandler, AgentLinkHandler>();
        services.AddHostedService<GatewayLifecycleService>();
        return services;
    }
}
