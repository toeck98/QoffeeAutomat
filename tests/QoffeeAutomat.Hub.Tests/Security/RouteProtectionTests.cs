using System.Net;
using System.Text.Json.Nodes;
using QoffeeAutomat.Hub.Api;

namespace QoffeeAutomat.Hub.Tests.Security;

/// <summary>
/// Every route below /api/v1 needs authentication, except the documented first-run setup and sign-in routes (T903). The routes are
/// enumerated from the endpoints that are really mapped, so a new route that is open by mistake fails here.
/// </summary>
[Trait("Category", "Security")]
public sealed class RouteProtectionTests
{
    [Fact]
    public async Task The_anonymous_routes_are_exactly_the_documented_setup_and_login_routes()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();

        IReadOnlyList<HubOperation> all = HubRoutes.All(host);
        string[] anonymous = [.. all.Where(operation => operation.Anonymous).Select(operation => operation.Key).Order(StringComparer.Ordinal)];
        string[] documented = [.. HubApi.AnonymousOperations.Order(StringComparer.Ordinal)];

        Assert.True(all.Count >= 15, "The test must see the API: " + all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " operations.");
        string[] expected = ["GET /api/v1/auth/setup", "POST /api/v1/auth/login", "POST /api/v1/auth/setup"];
        Assert.Equal(documented, anonymous);
        Assert.Equal(expected, documented);
    }

    [Fact]
    public async Task Every_other_operation_names_an_authorization_policy()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();

        foreach (HubOperation operation in HubRoutes.All(host).Where(operation => !operation.Anonymous))
        {
            Assert.True(operation.Policies.Count > 0, operation.Key + " has no authorization policy.");
        }
    }

    [Fact]
    public async Task Every_other_operation_answers_401_without_credentials()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        IReadOnlyList<HubOperation> protectedOperations = [.. HubRoutes.All(host).Where(operation => !operation.Anonymous)];

        Assert.NotEmpty(protectedOperations);
        foreach (HubOperation operation in protectedOperations)
        {
            using HttpResponseMessage response = await host.Anonymous.SendAsync(new HttpMethod(operation.Method), operation.Path, operation.Method == "GET" ? null : "{}");

            JsonNode problem = await HubAssert.ProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
            Assert.Contains("Sign in", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_made_up_session_or_a_made_up_token_is_no_credential()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        var forged = host.CreateClient();
        forged.Cookie = "qoffeeautomat.hub.session=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var guessed = host.CreateClient();
        guessed.BearerToken = "qht_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var garbage = host.CreateClient();
        garbage.BearerToken = "not a token";

        HubApiClient[] clients = [forged, guessed, garbage];
        foreach (HubApiClient client in clients)
        {
            using HttpResponseMessage response = await client.GetAsync("/api/v1/users");

            await HubAssert.ProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        }
    }

    [Fact]
    public async Task The_openapi_document_and_the_hub_identity_need_authentication_but_healthz_does_not()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();

        using HttpResponseMessage openApi = await host.Anonymous.GetAsync(HubRoutes.OpenApiPath);
        using HttpResponseMessage hub = await host.Anonymous.GetAsync("/api/v1/hub");
        using HttpResponseMessage health = await host.Anonymous.GetAsync("/healthz");

        await HubAssert.ProblemAsync(openApi, HttpStatusCode.Unauthorized, "unauthorized");
        await HubAssert.ProblemAsync(hub, HttpStatusCode.Unauthorized, "unauthorized");
        JsonNode body = await HubAssert.JsonAsync(health);
        Assert.Equal("ok", body["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_documented_open_routes_work_without_credentials()
    {
        await using HubTestHost host = HubTestHost.StartFresh();

        using HttpResponseMessage state = await host.Anonymous.GetAsync("/api/v1/auth/setup");
        using HttpResponseMessage login = await host.Anonymous.PostAsync("/api/v1/auth/login", HubApiClient.Json(("username", "nobody"), ("password", "not the password")));

        JsonNode available = await HubAssert.JsonAsync(state);
        Assert.True(available["available"]!.GetValue<bool>());
        await HubAssert.ProblemAsync(login, HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact]
    public async Task Api_answers_are_not_cached_and_carry_no_sniffing_hints()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();

        using HttpResponseMessage response = await host.Admin.GetAsync("/api/v1/hub");

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }
}
