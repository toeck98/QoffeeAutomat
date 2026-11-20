namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>
/// Decides where to go after signing in. The login page receives the page that the user wanted as the query parameter <c>returnUrl</c>, as a path
/// relative to the base address of the app (<c>agents/pc-1/commands</c>): an absolute path would leave the path prefix of Home Assistant ingress. Only
/// such a relative path is followed, never an address that names another site (an open redirect would let a link send a freshly signed-in user anywhere).
/// </summary>
public static class HubReturnTarget
{
    /// <summary>The page shown when there is no usable return address: the fleet.</summary>
    public const string Home = "agents";

    /// <summary>Resolves the return address.</summary>
    /// <param name="returnUrl">The value of the query parameter; may be null or hostile.</param>
    /// <returns>A path relative to the base address that does not start with a slash.</returns>
    public static string Resolve(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return Home;
        }

        string first = returnUrl.Split('/')[0];
        bool hostile = returnUrl[0] is '/' or '\\' or '?' or '#'
            || first.Contains(':', StringComparison.Ordinal)
            || returnUrl.Contains('\\', StringComparison.Ordinal)
            || returnUrl.Contains('\n', StringComparison.Ordinal)
            || returnUrl.Contains('\r', StringComparison.Ordinal);
        return hostile ? Home : returnUrl;
    }

    /// <summary>Builds the path of the login page that returns to a page.</summary>
    /// <param name="relativePath">The page, relative to the base address (what <c>NavigationManager.ToBaseRelativePath</c> returns).</param>
    /// <returns><c>login</c>, or <c>login?returnUrl=...</c> with the escaped page.</returns>
    public static string LoginPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        return relativePath.Length == 0 ? "login" : "login?returnUrl=" + Uri.EscapeDataString(relativePath);
    }
}
