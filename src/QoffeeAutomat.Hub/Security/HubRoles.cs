namespace QoffeeAutomat.Hub.Security;

/// <summary>The roles of the hub (PLAN.md S20, "Hub security"): <c>admin</c> may do everything, <c>operator</c> may view and run commands, <c>viewer</c> may only view.</summary>
public static class HubRoles
{
    /// <summary>May do everything: users, tokens, the audit log, the configuration.</summary>
    public const string Admin = "admin";

    /// <summary>May view everything and run commands.</summary>
    public const string Operator = "operator";

    /// <summary>May view.</summary>
    public const string Viewer = "viewer";

    /// <summary>All roles, from the most to the least powerful.</summary>
    public static IReadOnlyList<string> All { get; } = [Admin, Operator, Viewer];

    /// <summary>Tells whether a string is a role.</summary>
    /// <param name="role">The candidate; roles are lower case.</param>
    /// <returns>True for <c>admin</c>, <c>operator</c> and <c>viewer</c>.</returns>
    public static bool IsValid(string? role) => role is Admin or Operator or Viewer;

    /// <summary>Ranks a role: admin 3, operator 2, viewer 1, anything else 0.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The rank.</returns>
    public static int Rank(string? role) => role switch
    {
        Admin => 3,
        Operator => 2,
        Viewer => 1,
        _ => 0,
    };
}

/// <summary>The authorization policies of the hub: one per role. A policy lets in its role and every more powerful one.</summary>
public static class HubPolicies
{
    /// <summary>Any signed-in user or API token: viewer, operator and admin.</summary>
    public const string Viewer = "hub.viewer";

    /// <summary>Operator and admin.</summary>
    public const string Operator = "hub.operator";

    /// <summary>Admin only.</summary>
    public const string Admin = "hub.admin";

    /// <summary>The policy that a role needs at least.</summary>
    /// <param name="role">The role: <c>admin</c>, <c>operator</c> or <c>viewer</c>.</param>
    /// <returns>The policy name.</returns>
    public static string For(string role) => role switch
    {
        HubRoles.Admin => Admin,
        HubRoles.Operator => Operator,
        _ => Viewer,
    };
}
