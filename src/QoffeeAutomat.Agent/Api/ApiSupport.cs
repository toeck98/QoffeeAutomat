using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Api;

/// <summary>
/// Optimistic concurrency of the management API (PLAN.md S08): a read returns the revision of a document as
/// <c>ETag</c>, a write may carry it back in <c>If-Match</c>. A write whose revision is stale is refused with
/// 412 (<c>revision_mismatch</c>) and the current revision; creating a document whose id exists is 409
/// (<c>already_exists</c>). Without <c>If-Match</c> a write replaces whatever is stored. Scripts use the
/// SHA-256 of their bytes as ETag.
/// </summary>
public static class ApiETag
{
    /// <summary>The ETag of a document revision: the revision number in quotes.</summary>
    /// <param name="revision">The revision; null (a document that was never stored) counts as zero.</param>
    /// <returns>The header value.</returns>
    public static string ForRevision(int? revision)
    {
        return "\"" + (revision ?? 0).ToString(CultureInfo.InvariantCulture) + "\"";
    }

    /// <summary>The ETag of a script: its hash in quotes.</summary>
    /// <param name="hash">The SHA-256 of the file bytes in lower-case hex.</param>
    /// <returns>The header value.</returns>
    public static string ForHash(string hash)
    {
        return "\"" + hash + "\"";
    }

    /// <summary>Sets the <c>ETag</c> header of a response.</summary>
    /// <param name="response">The response.</param>
    /// <param name="etag">The value, see <see cref="ForRevision"/> and <see cref="ForHash"/>.</param>
    public static void Set(HttpResponse response, string etag)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers.ETag = etag;
    }

    /// <summary>Reads <c>If-Match</c> as a revision.</summary>
    /// <param name="request">The request.</param>
    /// <param name="expected">The revision the caller expects; null when the header is absent or <c>*</c>.</param>
    /// <returns>The 400 response when the header is not a revision; null when it is fine.</returns>
    public static IResult? ReadRevision(HttpRequest request, out int? expected)
    {
        ArgumentNullException.ThrowIfNull(request);

        expected = null;
        string? tag = FirstTag(request);
        if (tag is null)
        {
            return null;
        }

        if (!int.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out int revision))
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_if_match", "If-Match must be the ETag of the document, for example \"3\".");
        }

        expected = revision;
        return null;
    }

    /// <summary>Reads <c>If-Match</c> as the hash of a script.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The hash the caller expects; null when the header is absent or <c>*</c>.</returns>
    public static string? ReadHash(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return FirstTag(request);
    }

    /// <summary>The 412 response for a write whose revision is stale.</summary>
    /// <param name="currentRevision">The revision that is stored; null when the document does not exist.</param>
    /// <returns>The result.</returns>
    public static IResult Stale(int? currentRevision)
    {
        return ApiResults.Problem(
            StatusCodes.Status412PreconditionFailed,
            "revision_mismatch",
            "The document was changed since it was read. Read it again and repeat the change.",
            currentRevision: currentRevision);
    }

    private static string? FirstTag(HttpRequest request)
    {
        string header = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        string first = header.Split(',')[0].Trim();
        if (first == "*")
        {
            return null;
        }

        if (first.StartsWith("W/", StringComparison.Ordinal))
        {
            first = first[2..];
        }

        return first.Trim('"');
    }
}

/// <summary>Builds JSON values without reflection, for metric values and other loosely typed data.</summary>
public static class ApiJson
{
    /// <summary>The JSON value <c>null</c>.</summary>
    public static JsonElement Null { get; } = Parse("null");

    /// <summary>Converts a metric value (a number, a bool, a string or null) to JSON.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON element; <see cref="Null"/> for null and for values that JSON cannot represent.</returns>
    public static JsonElement Element(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    break;
                case string text:
                    writer.WriteStringValue(text);
                    break;
                case double number when double.IsFinite(number):
                    writer.WriteNumberValue(number);
                    break;
                case int number:
                    writer.WriteNumberValue(number);
                    break;
                case long number:
                    writer.WriteNumberValue(number);
                    break;
                case JsonElement element:
                    element.WriteTo(writer);
                    break;
                default:
                    writer.WriteNullValue();
                    break;
            }
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Converts a metric value to JSON, leaving out null.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The element, or null when the value is null.</returns>
    public static JsonElement? ElementOrNull(object? value)
    {
        return value is null ? null : Element(value);
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>Brings a draft document into the shape the store would give it, so that the validator judges only what the author wrote.</summary>
public static class ApiDocuments
{
    /// <summary>Sets the members that the store owns (schema version, revision, time, origin).</summary>
    /// <param name="document">The draft.</param>
    /// <returns>The draft as the store would stamp it, without revision and time.</returns>
    public static CommandDocument Normalize(CommandDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document with { SchemaVersion = DocumentSchema.CommandVersion, Revision = null, UpdatedAt = null, Origin = DocumentOrigins.Local };
    }

    /// <summary>Sets the members that the store owns.</summary>
    /// <param name="document">The draft.</param>
    /// <returns>The draft as the store would stamp it, without revision and time.</returns>
    public static SensorDocument Normalize(SensorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document with { SchemaVersion = DocumentSchema.CurrentVersion, Revision = null, UpdatedAt = null, Origin = DocumentOrigins.Local };
    }

    /// <summary>Converts the outcome of a validation to the response of <c>validate</c>.</summary>
    /// <param name="result">The validator's outcome.</param>
    /// <returns>The report.</returns>
    public static ValidationReport Report(ValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ValidationReport(
            result.IsValid,
            [.. result.Errors.Select(error => new ApiValidationError(error.Path, error.Code, error.Message))]);
    }

    /// <summary>The lookup that the validator uses to find cycles: the commands of the effective configuration.</summary>
    /// <param name="config">The configuration store.</param>
    /// <returns>The lookup.</returns>
    public static Func<string, CommandDocument?> CommandLookup(IConfigStore config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return id => config.Effective.GetDocument<CommandDocument>(id);
    }

    /// <summary>Turns the outcome of a save into the response of the API.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="result">The outcome of <see cref="ILocalConfigLayer.SaveAsync{T}"/>.</param>
    /// <param name="typeInfo">The type info of the document.</param>
    /// <param name="context">The request context, for the response headers.</param>
    /// <param name="created">True when the request creates the document: 201 with <c>Location</c>, and an existing id is a 409.</param>
    /// <param name="location">The URL of the document, for <c>Location</c>.</param>
    /// <returns>The result.</returns>
    public static IResult SaveOutcome<T>(SaveResult<T> result, JsonTypeInfo<T> typeInfo, HttpContext context, bool created, string location)
        where T : class, IConfigDocument
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(context);

        switch (result.Status)
        {
            case ChangeStatus.Applied:
                ApiETag.Set(context.Response, ApiETag.ForRevision(result.Document!.Revision));
                if (created)
                {
                    context.Response.Headers.Location = location;
                }

                return ApiResults.Json(result.Document!, typeInfo, created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            case ChangeStatus.Conflict:
                return created
                    ? ApiResults.Problem(StatusCodes.Status409Conflict, "already_exists", "A document with this id already exists. Use PUT to change it.", currentRevision: result.CurrentRevision)
                    : ApiETag.Stale(result.CurrentRevision);
            case ChangeStatus.NotFound:
                return ApiResults.NotFound("not_found", "The document does not exist.");
            default:
                return ApiResults.ValidationFailed(result.Errors);
        }
    }
}
