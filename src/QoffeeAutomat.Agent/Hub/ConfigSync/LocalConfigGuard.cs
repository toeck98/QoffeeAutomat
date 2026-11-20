using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.Config;

namespace QoffeeAutomat.Agent.Hub.ConfigSync;

/// <summary>The part of the local configuration that an endpoint changes; the hub's policy and its documents decide whether the write is allowed.</summary>
public enum LocalConfigArea
{
    /// <summary>A command; the route value <c>id</c> is the command id.</summary>
    Command,

    /// <summary>A custom sensor; the route value <c>id</c> is the sensor id.</summary>
    Sensor,

    /// <summary>A script file; the route value <c>name</c> is the file name.</summary>
    Script,

    /// <summary>The metric overrides; there is one document, whatever the route says.</summary>
    MetricOverrides,

    /// <summary>The restore of an earlier revision; the route values <c>kind</c> and <c>id</c> name the document. Settings are not affected.</summary>
    Revision,

    /// <summary>Several documents at once (an import, a template): refused while the local configuration is locked, otherwise allowed.</summary>
    Collection,
}

/// <summary>
/// Keeps the management API from changing the local configuration where the hub forbids it (PLAN.md S20, D-028, D-029, T908). Every write endpoint for
/// commands, sensors, scripts, metric overrides and modes carries this filter. With <c>local_config: locked</c> it answers 403
/// <c>local_config_locked</c>, with a document that the hub layer owns 409 <c>hub_document</c>. The filter sits in the API itself, so the same rule holds for
/// the browser, the command line and a request that arrives through the management tunnel. Reads, the agent settings, secrets and the hub endpoints are not
/// guarded: viewing, the password change and leaving the hub stay possible.
/// </summary>
public static class LocalConfigGuard
{
    /// <summary>Adds the guard to an endpoint that writes to the local configuration.</summary>
    /// <param name="builder">The endpoint.</param>
    /// <param name="area">What the endpoint changes.</param>
    /// <returns>The same builder.</returns>
    public static RouteHandlerBuilder GuardsLocalConfig(this RouteHandlerBuilder builder, LocalConfigArea area)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(async (context, next) =>
        {
            IResult? refusal = Check(context.HttpContext, area);
            return refusal ?? await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Decides whether a request may change the local configuration.</summary>
    /// <param name="context">The request.</param>
    /// <param name="area">What the endpoint changes.</param>
    /// <returns>The refusal, or null when the write may go on.</returns>
    public static IResult? Check(HttpContext context, LocalConfigArea area)
    {
        ArgumentNullException.ThrowIfNull(context);

        IHubPolicy policy = context.RequestServices.GetRequiredService<IHubPolicy>();
        if (policy.LocalConfigLocked)
        {
            return ApiResults.Problem(
                StatusCodes.Status403Forbidden,
                HubConfigProblemCodes.LocalConfigLocked,
                "The hub that manages this PC locked the local configuration. Commands, sensors, scripts and metric overrides can be changed on the hub only.");
        }

        IConfigStore config = context.RequestServices.GetRequiredService<IConfigStore>();
        (ConfigKind Kind, string Id)? target = TargetOf(context, area);
        if (target is { } owned && config.Hub.Owns(owned.Kind, owned.Id))
        {
            return ApiResults.Problem(
                StatusCodes.Status409Conflict,
                HubConfigProblemCodes.HubDocument,
                "This document comes from the hub and is read-only on this PC. Change it on the hub, or leave the hub and keep a copy.");
        }

        return null;
    }

    private static (ConfigKind Kind, string Id)? TargetOf(HttpContext context, LocalConfigArea area)
    {
        switch (area)
        {
            case LocalConfigArea.MetricOverrides:
                return (ConfigKind.MetricOverrides, ConfigIds.MetricOverrides);
            case LocalConfigArea.Command:
                return RouteTarget(context, ConfigKind.Command, "id");
            case LocalConfigArea.Sensor:
                return RouteTarget(context, ConfigKind.Sensor, "id");
            case LocalConfigArea.Script:
                return RouteTarget(context, ConfigKind.Script, "name");
            case LocalConfigArea.Revision:
                string? kind = context.Request.RouteValues["kind"] as string;
                ConfigKind? mapped = kind switch
                {
                    DocumentKinds.Command => ConfigKind.Command,
                    DocumentKinds.Sensor => ConfigKind.Sensor,
                    _ => null,
                };
                return mapped is { } configKind ? RouteTarget(context, configKind, "id") : null;
            default:
                return null;
        }
    }

    private static (ConfigKind Kind, string Id)? RouteTarget(HttpContext context, ConfigKind kind, string routeValue)
    {
        return context.Request.RouteValues[routeValue] is string id ? (kind, id) : null;
    }
}
