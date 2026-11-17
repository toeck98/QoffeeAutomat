using System.Net;
using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>
/// Keeps the management API and the web UI on this PC unless "remote UI" is switched on (PLAN.md D-018, S09 item 9): a
/// request from a non-loopback address is answered with 403 while <see cref="AgentSettingsDocument.RemoteUi"/> is not
/// true. That covers <c>/api/v1</c>, the OpenAPI document and every static file and client route of the UI (T401); only
/// <c>/healthz</c>, QoffeeLink with pairing (<c>/link</c>) and the local channel (<c>/local</c>, which accepts loopback
/// callers only) are never gated. The loopback listener only ever sees loopback callers, so the gate matters for the
/// HTTPS listener. A request without a remote address (in-process test server) counts as local.
/// </summary>
public sealed class RemoteAccessGate
{
    private static readonly string[] OpenPrefixes = [AgentHost.HealthPath, "/link", "/local"];

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

        if (IsGated(context.Request.Path)
            && !IsLocal(context.Connection.RemoteIpAddress)
            && _config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)?.RemoteUi != true)
        {
            return ApiResults.Problem(
                StatusCodes.Status403Forbidden,
                "remote_ui_disabled",
                "The management UI and API are only available from this PC. Enable remote UI in the settings to use them over the network.").ExecuteAsync(context);
        }

        return _next(context);
    }

    private static bool IsGated(PathString path)
    {
        foreach (string prefix in OpenPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLocal(IPAddress? address) => address is null || IPAddress.IsLoopback(address);
}
