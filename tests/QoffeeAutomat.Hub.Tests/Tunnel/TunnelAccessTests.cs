using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Tests.Tunnel;

/// <summary>The role rules and the path rules of the management tunnel (PLAN.md S20, T912), which the hub and the agent apply with the same code.</summary>
[Trait("Category", "Tunnel")]
public sealed class TunnelAccessTests
{
    [Theory]
    [InlineData("/api/v1/commands")]
    [InlineData("/api/v1/commands/set_level/run")]
    [InlineData("/api/v1/runs/01JD8Z3Q4V7N2K9M5X6C8B0A1E/stream")]
    [InlineData("/api/v1/")]
    [InlineData("/api/v1/commands/")]
    public void A_path_below_api_v1_is_valid(string path)
    {
        Assert.True(TunnelAccess.IsValidPath(path, out string? error), error);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/v2/commands")]
    [InlineData("/api/v1")]
    [InlineData("/API/v1/commands")]
    [InlineData("api/v1/commands")]
    [InlineData("/local/emit")]
    [InlineData("/api/v1/../healthz")]
    [InlineData("/api/v1/commands/../../healthz")]
    [InlineData("/api/v1/./commands")]
    [InlineData("/api/v1//commands")]
    [InlineData("/api/v1/commands?x=1")]
    [InlineData("/api/v1/commands#top")]
    [InlineData("/api/v1/commands%2F..%2Fhealthz")]
    [InlineData("/api/v1/commands\\..\\healthz")]
    [InlineData("/api/v1/commands\n")]
    public void A_path_outside_api_v1_or_with_tricks_is_not_valid(string path)
    {
        Assert.False(TunnelAccess.IsValidPath(path, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_path_that_is_too_long_is_not_valid()
    {
        Assert.False(TunnelAccess.IsValidPath(TunnelAccess.ApiPrefix + new string('a', TunnelAccess.MaxPathLength), out _));
    }

    [Theory]
    [InlineData("viewer", "GET", "/api/v1/commands", true)]
    [InlineData("viewer", "GET", "/api/v1/runs/abc/stream", true)]
    [InlineData("viewer", "POST", "/api/v1/commands/set_level/run", false)]
    [InlineData("viewer", "POST", "/api/v1/commands/test", false)]
    [InlineData("viewer", "PUT", "/api/v1/commands/set_level", false)]
    [InlineData("viewer", "PATCH", "/api/v1/settings", false)]
    [InlineData("viewer", "DELETE", "/api/v1/commands/set_level", false)]
    [InlineData("operator", "GET", "/api/v1/commands", true)]
    [InlineData("operator", "POST", "/api/v1/commands/set_level/run", true)]
    [InlineData("operator", "POST", "/api/v1/commands/test", true)]
    [InlineData("operator", "POST", "/api/v1/commands/validate", false)]
    [InlineData("operator", "POST", "/api/v1/commands/run", false)]
    [InlineData("operator", "POST", "/api/v1/commands/a/b/run", false)]
    [InlineData("operator", "PUT", "/api/v1/commands/set_level", false)]
    [InlineData("operator", "DELETE", "/api/v1/commands/set_level", false)]
    [InlineData("operator", "POST", "/api/v1/secrets", false)]
    [InlineData("admin", "GET", "/api/v1/commands", true)]
    [InlineData("admin", "POST", "/api/v1/commands/set_level/run", true)]
    [InlineData("admin", "PUT", "/api/v1/commands/set_level", true)]
    [InlineData("admin", "PATCH", "/api/v1/settings", true)]
    [InlineData("admin", "DELETE", "/api/v1/commands/set_level", true)]
    public void Each_role_may_do_what_the_specification_says(string role, string method, string path, bool allowed)
    {
        TunnelDecision decision = TunnelAccess.Check(role, method, path);

        Assert.Equal(allowed, decision.Allowed);
        if (!allowed)
        {
            Assert.Equal(403, decision.Status);
            Assert.Equal(HubErrorCodes.Forbidden, decision.Code);
        }
    }

    [Theory]
    [InlineData("admin", "POST", "/api/v1/auth/login")]
    [InlineData("admin", "POST", "/api/v1/auth/password")]
    [InlineData("admin", "GET", "/api/v1/auth/me")]
    [InlineData("admin", "POST", "/api/v1/auth/setup")]
    [InlineData("admin", "GET", "/api/v1/AUTH/one-time")]
    [InlineData("viewer", "GET", "/api/v1/auth/me")]
    public void The_account_of_the_agent_is_out_of_reach_for_every_role(string role, string method, string path)
    {
        TunnelDecision decision = TunnelAccess.Check(role, method, path);

        Assert.False(decision.Allowed);
        Assert.Equal(403, decision.Status);
    }

    [Theory]
    [InlineData("PATCH")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public void The_hub_link_of_the_agent_can_be_read_but_not_changed_through_the_tunnel(string method)
    {
        Assert.False(TunnelAccess.Check("admin", method, "/api/v1/hub").Allowed);
        Assert.False(TunnelAccess.Check("admin", method, "/api/v1/hub/leave").Allowed);
        Assert.True(TunnelAccess.Check("admin", "GET", "/api/v1/hub").Allowed);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("")]
    [InlineData("Admin")]
    [InlineData(null)]
    public void An_unknown_role_gets_nothing(string? role)
    {
        TunnelDecision decision = TunnelAccess.Check(role, "GET", "/api/v1/commands");

        Assert.False(decision.Allowed);
        Assert.Equal(403, decision.Status);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("TRACE")]
    [InlineData("")]
    [InlineData(null)]
    public void A_method_that_the_protocol_does_not_know_is_a_bad_request(string? method)
    {
        TunnelDecision decision = TunnelAccess.Check("admin", method, "/api/v1/commands");

        Assert.Equal(400, decision.Status);
    }

    [Fact]
    public void The_method_may_be_written_in_any_case()
    {
        Assert.True(TunnelAccess.Check("operator", "post", "/api/v1/commands/set_level/run").Allowed);
        Assert.False(TunnelAccess.Check("viewer", "delete", "/api/v1/commands/set_level").Allowed);
    }
}
