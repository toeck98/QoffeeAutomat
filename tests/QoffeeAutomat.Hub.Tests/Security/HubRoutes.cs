using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Tests.Security;

/// <summary>One operation of the hub API as the endpoint data source knows it.</summary>
/// <param name="Method">The HTTP method, upper case.</param>
/// <param name="Pattern">The route pattern, for example <c>/api/v1/users/{id}</c>.</param>
/// <param name="Anonymous">True when the endpoint is marked <c>AllowAnonymous</c>.</param>
/// <param name="Policies">The authorization policies on the endpoint (the group's and the endpoint's own).</param>
internal sealed record HubOperation(string Method, string Pattern, bool Anonymous, IReadOnlyList<string> Policies)
{
    public string Key => Method + " " + Pattern;

    /// <summary>The path with a concrete value for the route parameter, for example an id that does not exist.</summary>
    public string Path => HubRoutes.Concrete(Pattern);
}

/// <summary>Enumerates the operations of the hub API from the endpoints that are really mapped.</summary>
internal static class HubRoutes
{
    public const string OpenApiPath = "/openapi/v1.json";

    /// <summary>A concrete path for a route pattern: the route parameters get values that name nothing that exists (and a document kind that is valid).</summary>
    public static string Concrete(string pattern)
    {
        return pattern
            .Replace("{**rest}", "commands", StringComparison.Ordinal)
            .Replace("{id}", "unknown-id", StringComparison.Ordinal)
            .Replace("{kind}", "command", StringComparison.Ordinal)
            .Replace("{revision}", "1", StringComparison.Ordinal);
    }

    /// <summary>Every operation below <c>/api/v1</c> and the OpenAPI document, ordered by pattern and method.</summary>
    public static IReadOnlyList<HubOperation> All(HubTestHost host)
    {
        var operations = new List<HubOperation>();
        EndpointDataSource source = host.Service<EndpointDataSource>();
        foreach (RouteEndpoint endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            string? raw = endpoint.RoutePattern.RawText;
            if (raw is null || !(raw.StartsWith("/api/v1/", StringComparison.Ordinal) || raw == OpenApiPath))
            {
                continue;
            }

            bool anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            string[] policies = [.. endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy ?? string.Empty)];
            foreach (string method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                operations.Add(new HubOperation(method.ToUpperInvariant(), raw, anonymous, policies));
            }
        }

        return [.. operations.OrderBy(operation => operation.Pattern, StringComparer.Ordinal).ThenBy(operation => operation.Method, StringComparer.Ordinal)];
    }

    /// <summary>The least powerful role that a policy list needs.</summary>
    public static string RequiredRole(HubOperation operation)
    {
        string required = HubRoles.Viewer;
        foreach (string policy in operation.Policies)
        {
            string role = policy switch
            {
                HubPolicies.Admin => HubRoles.Admin,
                HubPolicies.Operator => HubRoles.Operator,
                _ => HubRoles.Viewer,
            };
            if (HubRoles.Rank(role) > HubRoles.Rank(required))
            {
                required = role;
            }
        }

        return required;
    }

    /// <summary>The OpenAPI builder's own opinion of the role, for the cross-check with the document.</summary>
    public static string? DocumentedRole(HubTestHost host, HubOperation operation)
    {
        EndpointDataSource source = host.Service<EndpointDataSource>();
        RouteEndpoint endpoint = source.Endpoints.OfType<RouteEndpoint>()
            .First(candidate => candidate.RoutePattern.RawText == operation.Pattern
                && (candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(operation.Method, StringComparer.OrdinalIgnoreCase) ?? operation.Method == "GET"));
        return HubOpenApiDocument.RequiredRole(endpoint);
    }
}
