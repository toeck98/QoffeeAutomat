using QoffeeAutomat.Agent.Security.Auth;

namespace QoffeeAutomat.Agent.Api.OpenApi;

/// <summary>
/// Publishes the OpenAPI document at <c>/openapi/v1.json</c> (PLAN.md S08). The document describes the whole
/// management API, so it is not public: it needs a session like the API itself.
/// </summary>
public static class OpenApiEndpoint
{
    /// <summary>Maps <c>GET /openapi/v1.json</c>.</summary>
    /// <param name="routes">The route builder.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(OpenApiDocumentBuilder.Path, (OpenApiDocumentProvider provider) => Results.Text(provider.Document, "application/json"))
            .RequireAuthorization(AuthNames.ManagementPolicy)
            .Describe("Meta", "getOpenApiDocument", "Returns this document.")
            .Produces(StatusCodes.Status200OK, null, "application/json");
    }
}
