using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Api;

/// <summary>Helpers for responses of the management API and the pairing endpoint (PLAN.md S08).</summary>
public static class ApiResults
{
    /// <summary>The media type of an error response (RFC 9457).</summary>
    public const string ProblemContentType = "application/problem+json";

    /// <summary>Status 422: the request is well formed but its content is not acceptable (the name of RFC 9110; the framework constant has had two names).</summary>
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

    /// <summary>
    /// Creates an RFC 9457 problem-details response with the machine-readable member <c>code</c>
    /// (PLAN.md S08: errors use problem details).
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="code">A stable machine-readable code such as <c>invalid_credentials</c>.</param>
    /// <param name="detail">A human-readable explanation; never contains secrets.</param>
    /// <param name="errors">Validation errors with JSON pointers into the request body (status 422).</param>
    /// <param name="currentRevision">The revision that is stored, when the request named a stale one.</param>
    /// <param name="runId">The id of a run that was recorded although the request was rejected.</param>
    /// <returns>The result.</returns>
    public static IResult Problem(
        int statusCode,
        string code,
        string detail,
        IReadOnlyList<ApiValidationError>? errors = null,
        int? currentRevision = null,
        string? runId = null)
    {
        var problem = new ApiProblem(
            "about:blank",
            ReasonPhrases.GetReasonPhrase(statusCode),
            statusCode,
            detail,
            code,
            errors is { Count: > 0 } ? errors : null,
            currentRevision,
            runId);
        return Results.Text(JsonSerializer.Serialize(problem, ApiJsonContext.Default.ApiProblem), ProblemContentType, statusCode: statusCode);
    }

    /// <summary>The 422 response of a document or draft that the validator rejected.</summary>
    /// <param name="errors">The validator's errors; their paths point into the request body.</param>
    /// <param name="pathPrefix">A JSON pointer that is put in front of every path when the document is nested in the request body (for example <c>/command</c>).</param>
    /// <returns>The result.</returns>
    public static IResult ValidationFailed(IEnumerable<ValidationError> errors, string pathPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(errors);

        ApiValidationError[] mapped = [.. errors.Select(error => new ApiValidationError(pathPrefix + error.Path, error.Code, error.Message))];
        return Problem(
            UnprocessableContent,
            "validation_failed",
            mapped.Length == 1 ? "The request has a validation error." : $"The request has {mapped.Length.ToString(CultureInfo.InvariantCulture)} validation errors.",
            mapped);
    }

    /// <summary>The 404 response for a resource that does not exist.</summary>
    /// <param name="code">The code, for example <c>unknown_command</c>.</param>
    /// <param name="detail">What was not found.</param>
    /// <returns>The result.</returns>
    public static IResult NotFound(string code, string detail)
    {
        return Problem(StatusCodes.Status404NotFound, code, detail);
    }

    /// <summary>
    /// Reads a request body of at most <paramref name="maxBytes"/> bytes and parses it as JSON.
    /// </summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="typeInfo">The source-generated type info; missing mandatory members make the body invalid.</param>
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

        (byte[]? body, IResult? failure) = await ReadBytesAsync(request, maxBytes, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return (null, failure);
        }

        return Parse(body, typeInfo);
    }

    /// <summary>
    /// Like <see cref="ReadBodyAsync{T}"/>, but an empty body is not an error: the value is null and there is no failure.
    /// </summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="typeInfo">The source-generated type info.</param>
    /// <param name="maxBytes">The size limit.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value (null for an empty body), or the problem response to send.</returns>
    public static async Task<(T? Value, IResult? Failure)> ReadOptionalBodyAsync<T>(
        HttpRequest request,
        JsonTypeInfo<T> typeInfo,
        int maxBytes,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(typeInfo);

        (byte[]? body, IResult? failure) = await ReadBytesAsync(request, maxBytes, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return (null, failure);
        }

        return body.AsSpan().Trim((byte)' ').IsEmpty ? (null, null) : Parse(body, typeInfo);
    }

    /// <summary>Reads a request body of at most <paramref name="maxBytes"/> bytes without interpreting it.</summary>
    /// <param name="request">The request.</param>
    /// <param name="maxBytes">The size limit.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes, or the problem response to send (413 for a large body).</returns>
    public static async Task<(byte[]? Bytes, IResult? Failure)> ReadBytesAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

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

        return body.Length > maxBytes ? (null, tooLarge) : (body, null);
    }

    private static (T? Value, IResult? Failure) Parse<T>(byte[] body, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            T? value = JsonSerializer.Deserialize(body, typeInfo);
            if (value is null)
            {
                return (null, Problem(StatusCodes.Status400BadRequest, "bad_request", "The request body is missing."));
            }

            return (value, null);
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
