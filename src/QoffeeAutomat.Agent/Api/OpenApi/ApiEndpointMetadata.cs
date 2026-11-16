using Microsoft.AspNetCore.Http.Metadata;
using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Agent.Api.OpenApi;

/// <summary>A query parameter of an operation, for the OpenAPI document.</summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Type">The JSON schema type: <c>string</c>, <c>integer</c> or <c>boolean</c>.</param>
/// <param name="Description">What the parameter does.</param>
public sealed record ApiQueryParameter(string Name, string Type, string Description);

/// <summary>The request body of an operation, for the OpenAPI document.</summary>
/// <param name="Type">The type of the body: a DTO, or <c>byte[]</c> for a file.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="Optional">True when the body may be left out.</param>
public sealed record ApiRequestBody(Type Type, string ContentType, bool Optional);

/// <summary>A request header of an operation, for the OpenAPI document.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Description">What the header does.</param>
public sealed record ApiHeaderParameter(string Name, string Description);

/// <summary>
/// Describes endpoints for the OpenAPI document (PLAN.md S08, T213). The agent builds the document from its
/// own endpoint data source, so a route that is added, removed or renamed shows up in the snapshot test.
/// </summary>
public static class ApiEndpointMetadata
{
    /// <summary>Names and documents an operation.</summary>
    /// <param name="builder">The endpoint.</param>
    /// <param name="tag">The group of the operation in the document.</param>
    /// <param name="operationId">A unique, stable name.</param>
    /// <param name="summary">One line that says what the operation does.</param>
    /// <returns>The same builder.</returns>
    public static RouteHandlerBuilder Describe(this RouteHandlerBuilder builder, string tag, string operationId, string summary)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithName(operationId).WithSummary(summary).WithTags(tag);
    }

    /// <summary>Declares a JSON response.</summary>
    /// <typeparam name="T">The type of the response body.</typeparam>
    /// <param name="builder">The endpoint.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>The same builder.</returns>
    public static RouteHandlerBuilder Returns<T>(this RouteHandlerBuilder builder, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Produces<T>(statusCode, "application/json");
    }

    /// <summary>Declares a response without a body.</summary>
    /// <param name="builder">The endpoint.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>The same builder.</returns>
    public static RouteHandlerBuilder ReturnsNothing(this RouteHandlerBuilder builder, int statusCode = StatusCodes.Status204NoContent)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Produces(statusCode);
    }

    /// <summary>Declares a response with another media type, for example a zip file or an event stream.</summary>
    /// <param name="builder">The endpoint.</param>
    /// <param name="contentType">The media type.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>The same builder.</returns>
    public static RouteHandlerBuilder ReturnsFile(this RouteHandlerBuilder builder, string contentType, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Produces<byte[]>(statusCode, contentType);
    }

    /// <summary>Declares the error statuses of an operation; they are answered with <see cref="ApiProblem"/>.</summary>
    /// <param name="builder">The endpoint.</param>
    /// <param name="statusCodes">The statuses.</param>
    /// <returns>The same builder.</returns>
    public static RouteHandlerBuilder Fails(this RouteHandlerBuilder builder, params int[] statusCodes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(statusCodes);

        foreach (int statusCode in statusCodes)
        {
            builder.Produces<ApiProblem>(statusCode, ApiResults.ProblemContentType);
        }

        return builder;
    }

    /// <summary>Declares the JSON request body.</summary>
    /// <typeparam name="T">The type of the body.</typeparam>
    /// <param name="builder">The endpoint.</param>
    /// <param name="optional">True when the body may be left out.</param>
    /// <returns>The same builder.</returns>
    public static RouteHandlerBuilder Takes<T>(this RouteHandlerBuilder builder, bool optional = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Not the framework's Accepts(): that metadata also takes part in endpoint matching and would answer 415 for a
        // request with another content type before the request is authenticated.
        return builder.WithMetadata(new ApiRequestBody(typeof(T), "application/json", optional));
    }

    /// <summary>Declares a request body with another media type.</summary>
    /// <param name="builder">The endpoint.</param>
    /// <param name="contentType">The media type.</param>
    /// <returns>The same builder.</returns>
    public static RouteHandlerBuilder TakesFile(this RouteHandlerBuilder builder, string contentType)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(new ApiRequestBody(typeof(byte[]), contentType, Optional: false));
    }

    /// <summary>Declares a query parameter.</summary>
    /// <param name="builder">The endpoint.</param>
    /// <param name="name">The parameter name.</param>
    /// <param name="type">The JSON schema type.</param>
    /// <param name="description">What the parameter does.</param>
    /// <returns>The same builder.</returns>
    public static RouteHandlerBuilder Query(this RouteHandlerBuilder builder, string name, string type, string description)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(new ApiQueryParameter(name, type, description));
    }

    /// <summary>Declares that the operation honours <c>If-Match</c>.</summary>
    /// <param name="builder">The endpoint.</param>
    /// <returns>The same builder.</returns>
    public static RouteHandlerBuilder HonoursIfMatch(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithMetadata(new ApiHeaderParameter("If-Match", "The ETag of the document as read; a stale value is refused with 412."))
            .Fails(StatusCodes.Status412PreconditionFailed);
    }

    /// <summary>The tags of an endpoint, in the order they were set.</summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <returns>The tags.</returns>
    internal static IReadOnlyList<string> TagsOf(Endpoint endpoint)
    {
        return endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? [];
    }
}
