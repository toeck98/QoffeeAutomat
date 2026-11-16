using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using QoffeeAutomat.Agent.Security.Auth;

namespace QoffeeAutomat.Agent.Tests.Api;

/// <summary>
/// The OpenAPI document of the management API (T213). It is built from the endpoints that are really mapped, and a
/// snapshot test makes every change of a route or a schema a deliberate one: when the document differs from
/// <c>openapi.v1.snapshot.json</c> the test fails, and the change is accepted by regenerating the snapshot with
/// <c>QOFFEE_UPDATE_SNAPSHOTS=1 dotnet test tests/QoffeeAutomat.Agent.Tests --filter Category=Api</c> and committing it.
/// </summary>
[Trait("Category", "Api")]
public sealed class OpenApiTests
{
    private const string DocumentPath = "/openapi/v1.json";

    [Fact]
    public async Task The_document_is_published_and_needs_a_session()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage anonymous = await host.SendAsync(HttpMethod.Get, DocumentPath, anonymous: true);
        using HttpResponseMessage response = await host.GetAsync(DocumentPath);
        JsonNode document = await ApiAssert.JsonAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("3.1.0", document["openapi"]!.GetValue<string>());
        Assert.NotNull(document["paths"]!["/api/v1/commands"]!["get"]);
        Assert.NotNull(document["paths"]!["/api/v1/commands"]!["post"]);
        Assert.NotNull(document["paths"]!["/api/v1/commands/{id}"]!["patch"]);
        Assert.NotNull(document["paths"]!["/api/v1/runs/{id}/stream"]!["get"]);
        Assert.NotNull(document["paths"]![DocumentPath]!["get"]);
        Assert.NotNull(document["components"]!["schemas"]!["CommandDocument"]);
        Assert.NotNull(document["components"]!["schemas"]!["ApiProblem"]);
    }

    [Fact]
    public async Task Every_route_below_api_v1_is_described_in_the_document_and_nothing_else_is()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using HttpResponseMessage response = await host.GetAsync(DocumentPath);
        JsonNode document = await ApiAssert.JsonAsync(response);

        var described = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string path, JsonNode? item) in document["paths"]!.AsObject())
        {
            foreach ((string method, _) in item!.AsObject())
            {
                described.Add(method.ToUpperInvariant() + " " + path);
            }
        }

        var mapped = new SortedSet<string>(StringComparer.Ordinal);
        EndpointDataSource source = host.Service<EndpointDataSource>();
        foreach (RouteEndpoint endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            string? raw = endpoint.RoutePattern.RawText;
            if (raw is null || !(raw.StartsWith("/api/v1/", StringComparison.Ordinal) || raw == DocumentPath))
            {
                continue;
            }

            foreach (string method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                mapped.Add(method.ToUpperInvariant() + " " + raw);
            }
        }

        Assert.True(mapped.Count > 40, "The test must see the API: " + mapped.Count.ToString(CultureInfo.InvariantCulture) + " operations.");
        Assert.Equal(mapped, described);
    }

    [Fact]
    public async Task Operation_ids_are_unique_and_every_operation_has_a_summary_and_a_response()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using HttpResponseMessage response = await host.GetAsync(DocumentPath);
        JsonNode document = await ApiAssert.JsonAsync(response);

        var ids = new List<string>();
        foreach ((string path, JsonNode? item) in document["paths"]!.AsObject())
        {
            foreach ((string method, JsonNode? operation) in item!.AsObject())
            {
                ids.Add(operation!["operationId"]!.GetValue<string>());
                Assert.False(string.IsNullOrWhiteSpace(operation["summary"]?.GetValue<string>()), method + " " + path + " has no summary.");
                Assert.NotEmpty(operation["responses"]!.AsObject());
            }
        }

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Operations_that_change_state_name_the_csrf_header_and_only_login_and_setup_are_open()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using HttpResponseMessage response = await host.GetAsync(DocumentPath);
        JsonNode document = await ApiAssert.JsonAsync(response);

        foreach ((string path, JsonNode? item) in document["paths"]!.AsObject())
        {
            foreach ((string method, JsonNode? operation) in item!.AsObject())
            {
                string label = method + " " + path;
                bool open = ManagementApi.AnonymousRoutes.Contains(path, StringComparer.Ordinal);
                Assert.True(open == (operation!["security"]!.AsArray().Count == 0), label + ": security does not match the routes that are open.");

                bool changesState = method != "get";
                bool namesCsrf = operation["parameters"]?.AsArray()
                    .Any(parameter => parameter!["$ref"]?.GetValue<string>() == "#/components/parameters/csrfHeader") ?? false;
                Assert.True(changesState == namesCsrf, label + ": the anti-forgery header must be named exactly on operations that change state.");
            }
        }
    }

    [Fact]
    public async Task The_document_matches_the_snapshot()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using HttpResponseMessage response = await host.GetAsync(DocumentPath);
        string actual = OpenApiSnapshot.Normalize(await response.Content.ReadAsStringAsync());
        string file = OpenApiSnapshot.File();

        if (Environment.GetEnvironmentVariable(OpenApiSnapshot.UpdateVariable) == "1")
        {
            await System.IO.File.WriteAllTextAsync(file, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        Assert.True(
            System.IO.File.Exists(file),
            "The snapshot " + file + " does not exist. Create it with " + OpenApiSnapshot.UpdateVariable + "=1 and commit it.");
        string expected = OpenApiSnapshot.Normalize(await System.IO.File.ReadAllTextAsync(file));
        string? difference = OpenApiSnapshot.Difference(expected, actual);
        Assert.True(
            difference is null,
            "The OpenAPI document changed (" + difference + "). If the change is intended, regenerate the snapshot with "
            + OpenApiSnapshot.UpdateVariable + "=1 and commit it.");
    }

    [Fact]
    public async Task The_snapshot_comparison_fails_for_a_changed_route_or_a_changed_schema()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using HttpResponseMessage response = await host.GetAsync(DocumentPath);
        string document = OpenApiSnapshot.Normalize(await response.Content.ReadAsStringAsync());

        string renamedRoute = document.Replace("\"/api/v1/commands/validate\"", "\"/api/v1/commands/check\"", StringComparison.Ordinal);
        string changedSchema = document.Replace("\"revision\"", "\"revisions\"", StringComparison.Ordinal);

        Assert.NotEqual(document, renamedRoute);
        Assert.NotEqual(document, changedSchema);
        Assert.Null(OpenApiSnapshot.Difference(document, document.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal)));
        Assert.NotNull(OpenApiSnapshot.Difference(document, renamedRoute));
        Assert.NotNull(OpenApiSnapshot.Difference(document, changedSchema));
    }
}

/// <summary>Locates, reads and compares the snapshot of the OpenAPI document.</summary>
internal static class OpenApiSnapshot
{
    public const string UpdateVariable = "QOFFEE_UPDATE_SNAPSHOTS";

    /// <summary>Line ends and the final line feed do not count as a change.</summary>
    public static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
    }

    /// <summary>The snapshot file in the source tree (found by walking up to <c>global.json</c>), so that regenerating it changes the file that is committed.</summary>
    public static string File()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory, "global.json")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory, "tests", "QoffeeAutomat.Agent.Tests", "Api", "openapi.v1.snapshot.json");
    }

    /// <summary>Describes the first line in which two documents differ; null when they are the same.</summary>
    public static string? Difference(string expected, string actual)
    {
        string[] left = Normalize(expected).Split('\n');
        string[] right = Normalize(actual).Split('\n');
        for (int index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            string a = index < left.Length ? left[index] : "<end>";
            string b = index < right.Length ? right[index] : "<end>";
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                return "line " + (index + 1).ToString(CultureInfo.InvariantCulture) + ": expected '" + a.Trim() + "' but found '" + b.Trim() + "'";
            }
        }

        return null;
    }
}
