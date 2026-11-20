namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>
/// The roles of the hub as the UI knows them (PLAN.md S20, "Hub security"): <c>admin</c> may do everything, <c>operator</c> may view and run
/// commands, <c>viewer</c> may only view. The UI hides what a role may not use; the hub refuses it as well, because the UI is no protection.
/// </summary>
public static class HubRoleNames
{
    /// <summary>May do everything: users, tokens, enrollment, the audit log, the configuration of the agents.</summary>
    public const string Admin = "admin";

    /// <summary>May view everything and run commands.</summary>
    public const string Operator = "operator";

    /// <summary>May view.</summary>
    public const string Viewer = "viewer";

    /// <summary>All roles, from the most to the least powerful.</summary>
    public static IReadOnlyList<string> All { get; } = [Admin, Operator, Viewer];

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

    /// <summary>Tells whether a role may do what a role of the required rank may do.</summary>
    /// <param name="role">The role of the user.</param>
    /// <param name="required">The role that the action needs at least.</param>
    /// <returns>True when <paramref name="role"/> is <paramref name="required"/> or more powerful.</returns>
    public static bool AtLeast(string? role, string required) => Rank(role) >= Rank(required) && Rank(role) > 0;
}
