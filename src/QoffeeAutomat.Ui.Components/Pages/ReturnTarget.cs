namespace QoffeeAutomat.Ui.Components.Pages;

/// <summary>
/// Decides where to go after signing in. The login page receives the address that the user wanted as the query
/// parameter <c>returnUrl</c>; only an address on this site is followed, never one that names another host (an open
/// redirect would let a link send a freshly signed-in user anywhere).
/// </summary>
public static class ReturnTarget
{
    /// <summary>The page shown when there is no usable return address.</summary>
    public const string Home = "/";

    /// <summary>Resolves the return address.</summary>
    /// <param name="returnUrl">The value of the query parameter; may be null or hostile.</param>
    /// <returns>A path on this site that starts with a single slash.</returns>
    public static string Resolve(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl)
            || returnUrl[0] != '/'
            || (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
            || returnUrl.Contains('\n', StringComparison.Ordinal)
            || returnUrl.Contains('\r', StringComparison.Ordinal))
        {
            return Home;
        }

        return returnUrl;
    }
}
