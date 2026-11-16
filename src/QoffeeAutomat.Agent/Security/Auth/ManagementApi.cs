using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Agent.Api.Runtime;
using QoffeeAutomat.Agent.Link.Pairing;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>
/// The root of the management API (PLAN.md S08, D-014, D-017). Every route below <c>/api/v1</c> requires a
/// session and, for methods that change state, the anti-forgery header. A route is reachable without a
/// session only when it is marked with <c>AllowAnonymous</c>, and S08 allows that for sign-in and setup
/// only. Later tasks map their endpoints on the group returned by <see cref="CreateGroup"/> so they
/// inherit the protection.
/// </summary>
public static class ManagementApi
{
    /// <summary>The base path of the API.</summary>
    public const string BasePath = "/api/v1";

    /// <summary>
    /// The routes below <see cref="BasePath"/> that need no session (PLAN.md S08): first-run setup (its
    /// state and the setup itself, only while setup is available and only with the setup token) and
    /// sign-in. Everything else must answer 401 without a session.
    /// </summary>
    public static readonly IReadOnlyList<string> AnonymousRoutes =
    [
        BasePath + "/auth/setup",
        BasePath + "/auth/login",
    ];

    /// <summary>Creates the protected <c>/api/v1</c> group.</summary>
    /// <param name="routes">The route builder.</param>
    /// <returns>The group; endpoints mapped on it require a session and the anti-forgery header.</returns>
    public static RouteGroupBuilder CreateGroup(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        RouteGroupBuilder group = routes.MapGroup(BasePath);
        group.RequireAuthorization(AuthNames.ManagementPolicy);
        group.AddEndpointFilter(new CsrfHeaderFilter());
        return group;
    }

    /// <summary>Maps the endpoints of the management API: authentication, pairing, configuration and runtime, and the OpenAPI document.</summary>
    /// <param name="routes">The route builder.</param>
    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder api = CreateGroup(routes);
        AuthEndpoints.Map(api);
        PairingEndpoints.MapManagement(api);
        ConfigurationEndpoints.Map(api);
        RuntimeEndpoints.Map(api);
        OpenApiEndpoint.Map(routes);
    }
}
