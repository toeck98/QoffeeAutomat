using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Tests.Security;

/// <summary>
/// A matrix of the roles per route (T903): for every operation that needs authentication, the least powerful role that may call it, written
/// out here by hand. The test calls every operation with every role (and with API tokens of every role) and checks that exactly the roles
/// of the matrix get through. An operation that is added without a row in the matrix fails the first test.
/// </summary>
[Trait("Category", "Security")]
public sealed class RoleMatrixTests
{
    /// <summary>The operations that need authentication and the least powerful role that may call each. The sign-in and setup routes are open to all.</summary>
    internal static readonly (string Method, string Pattern, string MinimumRole)[] Matrix =
    [
        ("GET", "/api/v1/hub", HubRoles.Viewer),
        ("GET", "/api/v1/auth/me", HubRoles.Viewer),
        ("POST", "/api/v1/auth/logout", HubRoles.Viewer),
        ("POST", "/api/v1/auth/password", HubRoles.Viewer),
        ("GET", "/api/v1/users", HubRoles.Admin),
        ("POST", "/api/v1/users", HubRoles.Admin),
        ("GET", "/api/v1/users/{id}", HubRoles.Admin),
        ("PATCH", "/api/v1/users/{id}", HubRoles.Admin),
        ("POST", "/api/v1/users/{id}/password", HubRoles.Admin),
        ("DELETE", "/api/v1/users/{id}", HubRoles.Admin),
        ("GET", "/api/v1/tokens", HubRoles.Admin),
        ("POST", "/api/v1/tokens", HubRoles.Admin),
        ("DELETE", "/api/v1/tokens/{id}", HubRoles.Admin),
        ("GET", "/api/v1/audit", HubRoles.Admin),
        ("GET", "/api/v1/enrollment-tokens", HubRoles.Admin),
        ("POST", "/api/v1/enrollment-tokens", HubRoles.Admin),
        ("DELETE", "/api/v1/enrollment-tokens/{id}", HubRoles.Admin),
        ("GET", "/api/v1/agents", HubRoles.Viewer),
        ("GET", "/api/v1/agents/{id}", HubRoles.Viewer),
        ("PATCH", "/api/v1/agents/{id}", HubRoles.Admin),
        ("POST", "/api/v1/agents/{id}/approve", HubRoles.Admin),
        ("POST", "/api/v1/agents/{id}/reject", HubRoles.Admin),
        ("POST", "/api/v1/agents/{id}/rotate-credential", HubRoles.Admin),
        ("DELETE", "/api/v1/agents/{id}", HubRoles.Admin),
        ("GET", "/api/v1/inventory", HubRoles.Viewer),
        ("GET", "/api/v1/inventory/{id}", HubRoles.Viewer),
        ("POST", "/api/v1/agents/{id}/run", HubRoles.Operator),

        // The tunnel to the management API of an agent (T912): the hub lets a viewer read and an operator call; the agent narrows that down to the rules of S20
        // (operators only run, everything else needs an administrator), which TunnelTests checks end to end.
        ("GET", "/api/v1/agents/{id}/agent-api/{**rest}", HubRoles.Viewer),
        ("POST", "/api/v1/agents/{id}/agent-api/{**rest}", HubRoles.Operator),
        ("PUT", "/api/v1/agents/{id}/agent-api/{**rest}", HubRoles.Operator),
        ("PATCH", "/api/v1/agents/{id}/agent-api/{**rest}", HubRoles.Operator),
        ("DELETE", "/api/v1/agents/{id}/agent-api/{**rest}", HubRoles.Operator),
        ("GET", "/api/v1/agents/{id}/desired-state", HubRoles.Viewer),
        ("GET", "/api/v1/agents/{id}/config-status", HubRoles.Viewer),
        ("POST", "/api/v1/agents/{id}/config-push", HubRoles.Operator),
        ("GET", "/api/v1/agents/{id}/metrics", HubRoles.Viewer),
        ("GET", "/api/v1/metrics/query", HubRoles.Viewer),
        ("GET", "/api/v1/rollouts", HubRoles.Viewer),
        ("GET", "/api/v1/profiles/{id}/rollout", HubRoles.Viewer),
        ("GET", "/api/v1/library", HubRoles.Viewer),
        ("GET", "/api/v1/library/{kind}/{id}", HubRoles.Viewer),
        ("GET", "/api/v1/library/{kind}/{id}/revisions", HubRoles.Viewer),
        ("PUT", "/api/v1/library/{kind}/{id}", HubRoles.Admin),
        ("DELETE", "/api/v1/library/{kind}/{id}", HubRoles.Admin),
        ("GET", "/api/v1/profiles", HubRoles.Viewer),
        ("POST", "/api/v1/profiles", HubRoles.Admin),
        ("GET", "/api/v1/profiles/{id}", HubRoles.Viewer),
        ("PUT", "/api/v1/profiles/{id}", HubRoles.Admin),
        ("DELETE", "/api/v1/profiles/{id}", HubRoles.Admin),
        ("GET", "/api/v1/profiles/{id}/revisions", HubRoles.Viewer),
        ("GET", "/api/v1/profiles/{id}/revisions/{revision}", HubRoles.Viewer),
        ("GET", "/api/v1/profiles/{id}/preview", HubRoles.Viewer),
        ("POST", "/api/v1/profiles/{id}/publish", HubRoles.Operator),
        ("POST", "/api/v1/profiles/{id}/rollback", HubRoles.Operator),
        ("GET", "/api/v1/assignments", HubRoles.Viewer),
        ("POST", "/api/v1/assignments", HubRoles.Admin),
        ("DELETE", "/api/v1/assignments/{id}", HubRoles.Admin),

        // Pairing of Home Assistant and the fleet tokens (T913): a fleet token lets Home Assistant run commands on every PC in its scope, so only an administrator manages them.
        ("POST", "/api/v1/fleet-pairings", HubRoles.Admin),
        ("DELETE", "/api/v1/fleet-pairings", HubRoles.Admin),
        ("GET", "/api/v1/fleet-tokens", HubRoles.Admin),
        ("DELETE", "/api/v1/fleet-tokens/{id}", HubRoles.Admin),

        // Fleet actions (T917): everyone reads; an operator runs a command on many PCs and cancels such a run; replacing the software of many PCs (and cancelling that) is the
        // administrator's. The cancel route admits an operator and refuses the cancel of an update inside the handler (FleetActionTests checks that).
        ("GET", "/api/v1/fleet-versions", HubRoles.Viewer),
        ("GET", "/api/v1/fleet-actions", HubRoles.Viewer),
        ("GET", "/api/v1/fleet-actions/{id}", HubRoles.Viewer),
        ("POST", "/api/v1/fleet-actions/bulk-runs", HubRoles.Operator),
        ("POST", "/api/v1/fleet-actions/updates", HubRoles.Admin),
        ("POST", "/api/v1/fleet-actions/{id}/cancel", HubRoles.Operator),
        ("GET", "/openapi/v1.json", HubRoles.Viewer),
    ];

    private static readonly string[] Roles = [HubRoles.Viewer, HubRoles.Operator, HubRoles.Admin];

    private static string KeyOf((string Method, string Pattern, string MinimumRole) entry) => entry.Method + " " + entry.Pattern;

    private static string PathOf((string Method, string Pattern, string MinimumRole) entry) => HubRoutes.Concrete(entry.Pattern);

    private static string? BodyOf((string Method, string Pattern, string MinimumRole) entry) => entry.Method is "POST" or "PATCH" or "PUT" ? "{}" : null;

    private static bool IsLogout((string Method, string Pattern, string MinimumRole) entry) => entry.Pattern == "/api/v1/auth/logout";

    private static async Task AssertRoleAsync(HubApiClient client, (string Method, string Pattern, string MinimumRole) entry, string role)
    {
        using HttpResponseMessage response = await client.SendAsync(new HttpMethod(entry.Method), PathOf(entry), BodyOf(entry));

        string label = KeyOf(entry) + " as " + role;
        if (HubRoles.Rank(role) >= HubRoles.Rank(entry.MinimumRole))
        {
            Assert.False(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                label + " must be allowed, but the answer was " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + await response.Content.ReadAsStringAsync());
        }
        else
        {
            await HubAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task The_matrix_has_a_row_for_every_protected_operation_and_the_policies_agree_with_it()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();

        IReadOnlyList<HubOperation> protectedOperations = [.. HubRoutes.All(host).Where(operation => !operation.Anonymous)];
        string[] mapped = [.. protectedOperations.Select(operation => operation.Key).Order(StringComparer.Ordinal)];
        string[] listed = [.. Matrix.Select(KeyOf).Order(StringComparer.Ordinal)];

        Assert.Equal(listed, mapped);
        foreach (HubOperation operation in protectedOperations)
        {
            string expected = Matrix.Single(entry => KeyOf(entry) == operation.Key).MinimumRole;
            Assert.Equal(expected, HubRoutes.RequiredRole(operation));
        }
    }

    [Fact]
    public async Task Every_role_may_call_exactly_the_operations_of_the_matrix()
    {
        await using HubTestHost host = await HubTestHost.StartAsync(withUsers: true);

        foreach (var entry in Matrix)
        {
            foreach (string role in Roles)
            {
                // Signing out ends the session, so every role gets a new one for it.
                HubApiClient client = IsLogout(entry) ? await host.FreshClientAsync(role) : host.ClientFor(role);

                await AssertRoleAsync(client, entry, role);
            }
        }
    }

    [Fact]
    public async Task API_tokens_of_every_role_follow_the_same_matrix_without_the_anti_forgery_header()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        var clients = new Dictionary<string, HubApiClient>(StringComparer.Ordinal);
        foreach (string role in Roles)
        {
            HubApiClient client = host.CreateClient();
            client.BearerToken = await host.CreateTokenAsync("matrix-" + role, role);
            client.SendCsrf = false;
            clients[role] = client;
        }

        foreach (var entry in Matrix)
        {
            // Changing a password needs a session, whatever the role of the token (see the next test).
            if (entry.Pattern == "/api/v1/auth/password")
            {
                continue;
            }

            foreach (string role in Roles)
            {
                await AssertRoleAsync(clients[role], entry, role);
            }
        }
    }

    [Fact]
    public async Task An_API_token_cannot_change_a_password_not_even_that_of_an_administrator()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        var client = host.CreateClient();
        client.BearerToken = await host.CreateTokenAsync("password-try", HubRoles.Admin);

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/auth/password",
            HubApiClient.Json(("currentPassword", HubTestHost.StrongPassword), ("newPassword", "a brand new password 123")));

        await HubAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "session_required");
    }

    [Fact]
    public async Task Each_policy_lets_in_its_role_and_every_more_powerful_one_and_nobody_else()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        IAuthorizationService authorization = host.Service<IAuthorizationService>();
        (string Policy, string[] Allowed)[] expectations =
        [
            (HubPolicies.Viewer, [HubRoles.Viewer, HubRoles.Operator, HubRoles.Admin]),
            (HubPolicies.Operator, [HubRoles.Operator, HubRoles.Admin]),
            (HubPolicies.Admin, [HubRoles.Admin]),
        ];
        string[] roles = [HubRoles.Viewer, HubRoles.Operator, HubRoles.Admin, "root", string.Empty];

        foreach ((string policy, string[] allowed) in expectations)
        {
            foreach (string role in roles)
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    new Claim[] { new(ClaimTypes.Name, "someone"), new(ClaimTypes.Role, role) },
                    "test"));

                AuthorizationResult result = await authorization.AuthorizeAsync(principal, policy);

                Assert.True(allowed.Contains(role, StringComparer.Ordinal) == result.Succeeded, policy + " for the role '" + role + "'.");
            }

            AuthorizationResult anonymous = await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), policy);
            Assert.False(anonymous.Succeeded, policy + " must not let in an unauthenticated principal.");
        }
    }

    [Fact]
    public void Roles_are_ranked_and_only_the_three_roles_are_valid()
    {
        Assert.True(HubRoles.Rank(HubRoles.Admin) > HubRoles.Rank(HubRoles.Operator));
        Assert.True(HubRoles.Rank(HubRoles.Operator) > HubRoles.Rank(HubRoles.Viewer));
        Assert.Equal(0, HubRoles.Rank("root"));
        Assert.Equal(0, HubRoles.Rank(null));
        Assert.True(HubRoles.IsValid("operator"));
        Assert.False(HubRoles.IsValid("Operator"));
        Assert.False(HubRoles.IsValid(string.Empty));
        Assert.False(HubRoles.IsValid(null));
        Assert.Equal(HubPolicies.Operator, HubPolicies.For(HubRoles.Operator));
    }
}
