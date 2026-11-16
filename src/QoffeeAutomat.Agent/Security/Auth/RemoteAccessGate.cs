using System.Net;
using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>
/// Keeps the management API on this PC unless "remote UI" is switched on (PLAN.md D-018, S09 item 9): a
/// request to <c>/api/v1</c> from a non-loopback address is answered with 403 while
/// <see cref="AgentSettingsDocument.RemoteUi"/> is not true. The loopback listener only ever sees
/// loopback callers, so the gate matters for the HTTPS listener. QoffeeLink and pairing are not
/// affected. A request without a remote address (in-process test server) counts as local.
/// </summary>
public sealed class RemoteAccessGate
{
    private readonly RequestDelegate _next;
    private readonly IConfigStore _config;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="config">The configuration, for the <c>remoteUi</c> setting.</param>
    public RemoteAccessGate(RequestDelegate next, IConfigStore config)
    {
        _next = next;
        _config = config;
    }

    /// <summary>Handles a request.</summary>
    /// <param name="context">The request context.</param>
    /// <returns>A task that completes when the request is handled.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Path.StartsWithSegments(ManagementApi.BasePath, StringComparison.OrdinalIgnoreCase)
            && !IsLocal(context.Connection.RemoteIpAddress)
            && _config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)?.RemoteUi != true)
        {
            return ApiResults.Problem(
                StatusCodes.Status403Forbidden,
                "remote_ui_disabled",
                "The management API is only available from this PC. Enable remote UI in the settings to use it over the network.").ExecuteAsync(context);
        }

        return _next(context);
    }

    private static bool IsLocal(IPAddress? address) => address is null || IPAddress.IsLoopback(address);
}
