using System.Text.Json;
using System.Text.Json.Serialization;

namespace QoffeeAutomat.Hub.Agents;

/// <summary>The error object of the hub endpoints that agents call: <c>{"error": {"code": "...", "message": "..."}}</c> (protocol/schema/hub/enroll_response.schema.json).</summary>
/// <param name="Code">A stable machine-readable code such as <c>invalid_token</c> or <c>revoked</c>.</param>
/// <param name="Message">A human-readable explanation; never contains a secret.</param>
public sealed record HubLinkErrorBody(string Code, string Message);

/// <summary>The body of an error answer of <c>/hub/v1/enroll</c> and <c>/hub/v1/agent</c>.</summary>
/// <param name="Error">The error.</param>
public sealed record HubLinkError(HubLinkErrorBody Error);

/// <summary>Source-generated JSON of the error body of the agent-facing endpoints (snake_case like the rest of QoffeeLink).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HubLinkError))]
internal sealed partial class HubLinkJsonContext : JsonSerializerContext
{
}

/// <summary>Writes the error answers of the agent-facing endpoints.</summary>
public static class HubLinkErrors
{
    /// <summary>Creates the result of an error answer.</summary>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">What went wrong.</param>
    /// <returns>The result; the content type is <c>application/json</c>.</returns>
    public static IResult Result(int statusCode, string code, string message)
    {
        string json = JsonSerializer.Serialize(new HubLinkError(new HubLinkErrorBody(code, message)), HubLinkJsonContext.Default.HubLinkError);
        return Results.Text(json, "application/json", statusCode: statusCode);
    }

    /// <summary>Writes an error answer to the response.</summary>
    /// <param name="context">The request context.</param>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">What went wrong.</param>
    /// <returns>A task that ends when the response is written.</returns>
    public static Task WriteAsync(HttpContext context, int statusCode, string code, string message)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Result(statusCode, code, message).ExecuteAsync(context);
    }
}
