using System.Security.Claims;

namespace QoffeeAutomat.Agent.Hub.Tunnel;

/// <summary>
/// The hub user behind a request that arrived through the management tunnel (PLAN.md S20). The tunnel puts it into <see cref="HttpContext.Items"/> of the in-process
/// request; the session authentication handler turns it into the principal of the request, so the request passes the same authorization policy, filters and endpoints as
/// a request of the browser. <see cref="HttpContext.Items"/> never comes from the network, so no client can present an identity.
/// </summary>
/// <param name="User">The hub user name.</param>
/// <param name="Role">One of <see cref="QoffeeAutomat.Contracts.Hub.TunnelRoles"/>.</param>
public sealed record TunnelIdentity(string User, string Role)
{
    /// <summary>The key in <see cref="HttpContext.Items"/>.</summary>
    public const string ItemKey = "QoffeeAutomat.Agent.TunnelIdentity";

    /// <summary>The claim that marks a principal as a hub user of the tunnel.</summary>
    public const string ClaimType = "urn:qoffeeautomat:tunnel-user";

    /// <summary>The identity of a request, if it came through the tunnel.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The identity, or null for every other request.</returns>
    public static TunnelIdentity? Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(ItemKey, out object? value) ? value as TunnelIdentity : null;
    }

    /// <summary>Builds the principal for the authentication system.</summary>
    /// <param name="scheme">The authentication scheme name.</param>
    /// <returns>The principal with the hub user as name, the role and the tunnel claim.</returns>
    public ClaimsPrincipal ToPrincipal(string scheme)
    {
        Claim[] claims =
        [
            new Claim(ClaimTypes.Name, "hub:" + User),
            new Claim(ClaimTypes.Role, Role),
            new Claim(ClaimType, User),
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }
}
