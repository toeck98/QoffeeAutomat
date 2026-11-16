using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.WebUtilities;
using QoffeeAutomat.Agent.Security.Auth;
using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Agent.Api.OpenApi;

/// <summary>
/// Builds the OpenAPI 3.1 document of the management API from the endpoints that the agent really maps, so the
/// document cannot drift from the routes (PLAN.md S08, T213). The operations come from the route patterns and the
/// metadata of <see cref="ApiEndpointMetadata"/>; the schemas are generated from the source-generated type
/// information of <see cref="ApiJsonContext"/> and <see cref="ApiDocumentJsonContext"/>, so a changed DTO changes the
/// document. The output is deterministic (sorted, no timestamps, no version of the agent), which is what lets a
/// snapshot test make every change of the API deliberate.
/// </summary>
public static class OpenApiDocumentBuilder
{
    /// <summary>The path at which the agent publishes the document.</summary>
    public const string Path = "/openapi/v1.json";

    private const string ApiPrefix = ManagementApi.BasePath + "/";
    private const string CsrfParameter = "csrfHeader";
    private const string SchemaPrefix = "#/components/schemas/";

    private static readonly string[] MethodOrder = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    /// <summary>Builds the document.</summary>
    /// <param name="endpoints">The endpoints of the application; those below <c>/api/v1</c> and the document itself are described.</param>
    /// <returns>The document as indented JSON with LF line ends and a final line feed.</returns>
    public static string Build(IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var referenced = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var operations = new List<(string Path, string Method, JsonObject Operation)>();
        foreach (RouteEndpoint endpoint in endpoints.OfType<RouteEndpoint>())
        {
            string? raw = endpoint.RoutePattern.RawText;
            if (raw is null || !(raw.StartsWith(ApiPrefix, StringComparison.Ordinal) || string.Equals(raw, Path, StringComparison.Ordinal)))
            {
                continue;
            }

            IReadOnlyList<string> methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (string method in methods)
            {
                string upper = method.ToUpperInvariant();
                operations.Add((raw, upper, BuildOperation(endpoint, upper, referenced)));
            }
        }

        var paths = new JsonObject();
        foreach (IGrouping<string, (string Path, string Method, JsonObject Operation)> group in operations
            .OrderBy(operation => operation.Path, StringComparer.Ordinal)
            .GroupBy(operation => operation.Path, StringComparer.Ordinal))
        {
            var item = new JsonObject();
            foreach ((string _, string method, JsonObject operation) in group.OrderBy(entry => MethodRank(entry.Method)))
            {
                item[method.ToLowerInvariant()] = operation;
            }

            paths[group.Key] = item;
        }

        var schemas = new JsonObject();
        foreach ((string name, Type type) in referenced)
        {
            schemas[name] = SchemaOf(type);
        }

        var root = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "QoffeeAutomat management API",
                ["version"] = "1",
                ["description"] = "The management API of the QoffeeAutomat agent. JSON with camelCase members; errors are RFC 9457 problem details. Every operation needs a session cookie, and operations that change state need the header X-QoffeeAutomat-Csrf: 1.",
            },
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["schemas"] = schemas,
                ["parameters"] = new JsonObject
                {
                    [CsrfParameter] = new JsonObject
                    {
                        ["name"] = CsrfHeaderFilter.HeaderName,
                        ["in"] = "header",
                        ["required"] = true,
                        ["description"] = "Anti-forgery header; the value must be 1.",
                        ["schema"] = new JsonObject { ["type"] = "string", ["const"] = CsrfHeaderFilter.HeaderValue },
                    },
                },
                ["securitySchemes"] = new JsonObject
                {
                    ["sessionCookie"] = new JsonObject
                    {
                        ["type"] = "apiKey",
                        ["in"] = "cookie",
                        ["name"] = SessionCookie.Name,
                    },
                },
            },
        };

        return Write(root);
    }

    private static int MethodRank(string method)
    {
        int index = Array.IndexOf(MethodOrder, method);
        return index < 0 ? MethodOrder.Length : index;
    }

    private static JsonObject BuildOperation(RouteEndpoint endpoint, string method, SortedDictionary<string, Type> referenced)
    {
        var operation = new JsonObject();

        IReadOnlyList<string> tags = ApiEndpointMetadata.TagsOf(endpoint);
        if (tags.Count > 0)
        {
            operation["tags"] = Strings(tags);
        }

        if (endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary is { } summary)
        {
            operation["summary"] = summary;
        }

        operation["operationId"] = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
            ?? SynthesizeOperationId(method, endpoint.RoutePattern);

        JsonArray parameters = BuildParameters(endpoint, method);
        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        if (endpoint.Metadata.GetMetadata<ApiRequestBody>() is { } body)
        {
            operation["requestBody"] = new JsonObject
            {
                ["required"] = !body.Optional,
                ["content"] = new JsonObject { [body.ContentType] = new JsonObject { ["schema"] = SchemaReference(body.Type, referenced) } },
            };
        }

        operation["responses"] = BuildResponses(endpoint, referenced);

        bool anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        operation["security"] = anonymous
            ? new JsonArray()
            : new JsonArray(new JsonObject { ["sessionCookie"] = new JsonArray() });
        return operation;
    }

    private static JsonArray BuildParameters(RouteEndpoint endpoint, string method)
    {
        var parameters = new JsonArray();
        foreach (RoutePatternParameterPart part in endpoint.RoutePattern.Parameters)
        {
            parameters.Add(new JsonObject
            {
                ["name"] = part.Name,
                ["in"] = "path",
                ["required"] = true,
                ["schema"] = new JsonObject { ["type"] = "string" },
            });
        }

        foreach (ApiQueryParameter query in endpoint.Metadata.GetOrderedMetadata<ApiQueryParameter>())
        {
            parameters.Add(new JsonObject
            {
                ["name"] = query.Name,
                ["in"] = "query",
                ["required"] = false,
                ["description"] = query.Description,
                ["schema"] = new JsonObject { ["type"] = query.Type },
            });
        }

        foreach (ApiHeaderParameter header in endpoint.Metadata.GetOrderedMetadata<ApiHeaderParameter>())
        {
            parameters.Add(new JsonObject
            {
                ["name"] = header.Name,
                ["in"] = "header",
                ["required"] = false,
                ["description"] = header.Description,
                ["schema"] = new JsonObject { ["type"] = "string" },
            });
        }

        if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)))
        {
            parameters.Add(new JsonObject { ["$ref"] = "#/components/parameters/" + CsrfParameter });
        }

        return parameters;
    }

    private static JsonObject BuildResponses(RouteEndpoint endpoint, SortedDictionary<string, Type> referenced)
    {
        var responses = new JsonObject();
        IEnumerable<IGrouping<int, IProducesResponseTypeMetadata>> byStatus = endpoint.Metadata
            .GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .GroupBy(metadata => metadata.StatusCode)
            .OrderBy(group => group.Key);

        foreach (IGrouping<int, IProducesResponseTypeMetadata> group in byStatus)
        {
            var response = new JsonObject { ["description"] = ReasonPhrases.GetReasonPhrase(group.Key) };
            var content = new JsonObject();
            foreach (IProducesResponseTypeMetadata metadata in group)
            {
                foreach (string contentType in metadata.ContentTypes.Order(StringComparer.Ordinal))
                {
                    content[contentType] = new JsonObject { ["schema"] = SchemaReference(metadata.Type, referenced) };
                }
            }

            if (content.Count > 0)
            {
                response["content"] = content;
            }

            responses[group.Key.ToString(CultureInfo.InvariantCulture)] = response;
        }

        if (responses.Count == 0)
        {
            responses["200"] = new JsonObject { ["description"] = "OK" };
        }

        return responses;
    }

    /// <summary>The schema of a body: a reference to a component for the DTOs, <c>binary</c> for bytes, anything for the rest.</summary>
    private static JsonObject SchemaReference(Type? type, SortedDictionary<string, Type> referenced)
    {
        if (type is null || type == typeof(void))
        {
            return new JsonObject();
        }

        if (type == typeof(byte[]))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "binary" };
        }

        if (TypeInfoOf(type) is null)
        {
            return new JsonObject();
        }

        referenced[type.Name] = type;
        return new JsonObject { ["$ref"] = SchemaPrefix + type.Name };
    }

    private static JsonTypeInfo? TypeInfoOf(Type type)
    {
        return ApiDocumentJsonContext.Default.GetTypeInfo(type) ?? ApiJsonContext.Default.GetTypeInfo(type);
    }

    private static JsonNode SchemaOf(Type type)
    {
        JsonTypeInfo? info = TypeInfoOf(type);
        if (info is null)
        {
            return new JsonObject();
        }

        try
        {
            return JsonSchemaExporter.GetJsonSchemaAsNode(info, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            // A type that the exporter cannot describe is documented as "anything" instead of failing the whole document.
            return new JsonObject();
        }
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }

    /// <summary>A name for an operation that has none: <c>getCommandsById</c> for <c>GET /api/v1/commands/{id}</c>.</summary>
    private static string SynthesizeOperationId(string method, RoutePattern pattern)
    {
        var name = new StringBuilder(method.ToLowerInvariant());
        foreach (RoutePatternPathSegment segment in pattern.PathSegments)
        {
            foreach (RoutePatternPart part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal when literal.Content is not ("api" or "v1"):
                        name.Append(char.ToUpperInvariant(literal.Content[0])).Append(literal.Content.AsSpan(1));
                        break;
                    case RoutePatternParameterPart parameter:
                        name.Append("By").Append(char.ToUpperInvariant(parameter.Name[0])).Append(parameter.Name.AsSpan(1));
                        break;
                }
            }
        }

        return name.ToString();
    }

    private static string Write(JsonNode root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            root.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}

/// <summary>Keeps the OpenAPI document that the agent publishes; it is built once, on first use, when every route is mapped.</summary>
public sealed class OpenApiDocumentProvider
{
    private readonly Lazy<string> _document;

    /// <summary>Creates the provider.</summary>
    /// <param name="endpoints">The endpoints of the application.</param>
    public OpenApiDocumentProvider(EndpointDataSource endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        _document = new Lazy<string>(() => OpenApiDocumentBuilder.Build(endpoints.Endpoints), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The document.</summary>
    public string Document => _document.Value;
}
