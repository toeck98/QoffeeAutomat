using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Hub.Api;

/// <summary>Helpers for responses of the hub API: JSON bodies and RFC 9457 problem details (PLAN.md S08, S20).</summary>
public static class HubApiResults
{
    /// <summary>The media type of an error response (RFC 9457).</summary>
    public const string ProblemContentType = "application/problem+json";

    /// <summary>Status 422: the request is well formed but its content is not acceptable.</summary>
    public const int UnprocessableContent = 422;

    /// <summary>Serializes a value with a source-generated type info and returns it as <c>application/json</c>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="typeInfo">The source-generated type info.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>The result.</returns>
    public static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return Results.Text(JsonSerializer.Serialize(value, typeInfo), "application/json", statusCode: statusCode);
    }

    /// <summary>Creates an RFC 9457 problem-details response with the machine-readable member <c>code</c>.</summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="code">A stable machine-readable code such as <c>invalid_credentials</c>.</param>
    /// <param name="detail">A human-readable explanation; never contains secrets.</param>
    /// <param name="errors">Validation errors with JSON pointers into the request body (status 422).</param>
    /// <returns>The result.</returns>
    public static IResult Problem(int statusCode, string code, string detail, IReadOnlyList<ApiValidationError>? errors = null)
    {
        var problem = new ApiProblem(
            "about:blank",
            ReasonPhrases.GetReasonPhrase(statusCode),
            statusCode,
            detail,
            code,
            errors is { Count: > 0 } ? errors : null);
        return Results.Text(JsonSerializer.Serialize(problem, HubApiJsonContext.Default.ApiProblem), ProblemContentType, statusCode: statusCode);
    }

    /// <summary>The 422 response for a request body whose content is not acceptable.</summary>
    /// <param name="path">A JSON pointer to the offending member, for example <c>/password</c>.</param>
    /// <param name="code">A stable code such as <c>weak_password</c>, <c>invalid</c> or <c>required</c>.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The result.</returns>
    public static IResult Invalid(string path, string code, string message)
    {
        return Problem(
            UnprocessableContent,
            "validation_failed",
            "The request has a validation error.",
            [new ApiValidationError(path, code, message)]);
    }

    /// <summary>The 404 response for a resource that does not exist.</summary>
    /// <param name="code">The code, for example <c>unknown_user</c>.</param>
    /// <param name="detail">What was not found.</param>
    /// <returns>The result.</returns>
    public static IResult NotFound(string code, string detail) => Problem(StatusCodes.Status404NotFound, code, detail);

    /// <summary>The 429 response of a lockout, with <c>Retry-After</c>.</summary>
    /// <param name="context">The request context.</param>
    /// <param name="retryAfter">The time left of the lockout.</param>
    /// <returns>The result.</returns>
    public static IResult LockedOut(HttpContext context, TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(context);

        int seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Problem(StatusCodes.Status429TooManyRequests, "locked_out", "Too many failed attempts. Try again later.");
    }

    /// <summary>Reads a request body of at most <paramref name="maxBytes"/> bytes and parses it as JSON.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="typeInfo">The source-generated type info.</param>
    /// <param name="maxBytes">The size limit.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or the problem response to send (413 for a large body, 400 for invalid JSON).</returns>
    public static async Task<(T? Value, IResult? Failure)> ReadBodyAsync<T>(
        HttpRequest request,
        JsonTypeInfo<T> typeInfo,
        int maxBytes,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(typeInfo);

        IResult tooLarge = Problem(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "The request body is too large.");
        if (request.ContentLength > maxBytes)
        {
            return (null, tooLarge);
        }

        IHttpMaxRequestBodySizeFeature? limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = maxBytes;
        }

        byte[] body;
        try
        {
            body = await ReadAtMostAsync(request.Body, maxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException)
        {
            return (null, tooLarge);
        }

        if (body.Length > maxBytes)
        {
            return (null, tooLarge);
        }

        try
        {
            T? value = JsonSerializer.Deserialize(body, typeInfo);
            return value is null
                ? (null, Problem(StatusCodes.Status400BadRequest, "bad_request", "The request body is missing."))
                : (value, null);
        }
        catch (JsonException)
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "bad_request", "The request body is not valid JSON or lacks a required member."));
        }
    }

    private static async Task<byte[]> ReadAtMostAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                break;
            }
        }

        return buffer.ToArray();
    }
}
