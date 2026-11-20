using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Metrics.Prometheus;

/// <summary>
/// <c>GET /metrics</c> for Prometheus (PLAN.md S20, T911). The route is outside <c>/api/v1</c> and is not part of the OpenAPI document. It needs an API token that was created in the
/// hub UI with the role <c>viewer</c> (a read-only token), sent as <c>Authorization: Bearer qht_...</c>. A request without a valid token gets 401, a session cookie does not count, and a token of
/// a more powerful role gets 403, so that a token that can run commands is never kept in a scrape configuration.
/// </summary>
public static class PrometheusEndpoint
{
    /// <summary>The path of the endpoint.</summary>
    public const string Path = "/metrics";

    /// <summary>Maps the endpoint.</summary>
    /// <param name="routes">The route builder.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, ExportAsync)
            .RequireAuthorization(HubPolicies.Viewer)
            .WithName("prometheusMetrics");
    }

    private static async Task<IResult> ExportAsync(HttpContext context, PrometheusExporter exporter)
    {
        if (HubPrincipal.Method(context.User) != AuthMethods.Token)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return HubApiResults.Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Send a read-only API token as Authorization: Bearer <token>.");
        }

        if (HubPrincipal.Role(context.User) != HubRoles.Viewer)
        {
            return HubApiResults.Problem(StatusCodes.Status403Forbidden, "forbidden", "Use an API token with the role viewer for the metrics export.");
        }

        PrometheusTextWriter document = await exporter.ExportAsync(context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Bytes(document.ToBytes(), PrometheusText.ContentType);
    }
}
