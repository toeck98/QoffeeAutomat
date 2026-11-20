using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Api.Users;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Enrollment;
using QoffeeAutomat.Hub.Fleet;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Tunnel;

namespace QoffeeAutomat.Hub.Api;

/// <summary>
/// The root of the hub API (PLAN.md S08, S20, D-017). Every route below <c>/api/v1</c> requires a session or an API token with at least
/// the role of its policy, and, for methods that change state and requests that use the session cookie, the anti-forgery header. A route is
/// reachable without authentication only when it is marked with <c>AllowAnonymous</c>, and only the routes of <see cref="AnonymousOperations"/> are:
/// first-run setup and sign-in. Later tasks map their endpoints on the group returned by <see cref="CreateGroup"/> so they inherit the
/// protection, and name the role they need with <c>RequireAuthorization(HubPolicies.Operator)</c> or <c>RequireAuthorization(HubPolicies.Admin)</c>.
/// </summary>
public static class HubApi
{
    /// <summary>The base path of the API.</summary>
    public const string BasePath = "/api/v1";

    /// <summary>
    /// The operations below <see cref="BasePath"/> that need no authentication, as <c>METHOD path</c>: first-run setup (its state and the
    /// setup itself, only while the hub has no account and only with the setup token) and sign-in. Every other operation must answer 401
    /// without credentials. A test enumerates the routes and compares them with this list.
    /// </summary>
    public static readonly IReadOnlyList<string> AnonymousOperations =
    [
        "GET " + BasePath + "/auth/setup",
        "POST " + BasePath + "/auth/setup",
        "POST " + BasePath + "/auth/login",
    ];

    /// <summary>Creates the protected <c>/api/v1</c> group.</summary>
    /// <param name="routes">The route builder.</param>
    /// <returns>The group; endpoints mapped on it require authentication and the anti-forgery header.</returns>
    public static RouteGroupBuilder CreateGroup(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        RouteGroupBuilder group = routes.MapGroup(BasePath);
        group.RequireAuthorization(HubPolicies.Viewer);
        group.AddEndpointFilter(new CsrfHeaderFilter());
        return group;
    }

    /// <summary>Maps the endpoints of the hub API and the OpenAPI document.</summary>
    /// <param name="routes">The route builder.</param>
    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder api = CreateGroup(routes);
        HubInfoEndpoints.Map(api);
        AuthEndpoints.Map(api);
        UserEndpoints.Map(api);
        TokenEndpoints.Map(api);
        AuditEndpoints.Map(api);
        EnrollmentTokenEndpoints.Map(api);
        AgentEndpoints.Map(api);
        FleetEndpoints.Map(api);
        FleetActionEndpoints.Map(api);
        GatewayEndpoints.Map(api);
        TunnelEndpoints.Map(api);
        ConfigEndpoints.Map(api);
        RolloutEndpoints.Map(api);
        MetricEndpoints.Map(api);
        MapOpenApi(routes);
    }

    private static void MapOpenApi(IEndpointRouteBuilder routes)
    {
        routes.MapGet(HubOpenApiDocument.Path, (HubOpenApiProvider provider) => Results.Text(provider.Document, "application/json"))
            .RequireAuthorization(HubPolicies.Viewer)
            .Describe("Meta", "getOpenApiDocument", "Returns this document.")
            .Produces(StatusCodes.Status200OK, null, "application/json");
    }
}
