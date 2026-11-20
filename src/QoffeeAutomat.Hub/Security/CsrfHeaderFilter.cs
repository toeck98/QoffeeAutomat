using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Api;

namespace QoffeeAutomat.Hub.Security;

/// <summary>
/// Anti-forgery protection for the hub API (PLAN.md S09 item 1): every request whose method can change state must carry the header
/// <see cref="HeaderName"/> with the value <see cref="HeaderValue"/>. A browser cannot add a custom header to a cross-site request without
/// a CORS preflight, and the hub answers no preflight, so a foreign page cannot forge such a request even though the session cookie would
/// travel with it. If the request names an origin, it must be the origin of the hub itself (as seen through a trusted proxy). The cookie
/// is also <c>SameSite=Strict</c>. The filter is attached to the whole <c>/api/v1</c> group, so it also covers sign-in and setup. Requests
/// that are authenticated by an API token are exempt: a token is no ambient credential that a browser adds by itself, and a request with a
/// token ignores the session cookie.
/// </summary>
public sealed class CsrfHeaderFilter : IEndpointFilter
{
    /// <summary>The name of the required header.</summary>
    public const string HeaderName = ApiHeaders.CsrfName;

    /// <summary>The required header value.</summary>
    public const string HeaderValue = ApiHeaders.CsrfValue;

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext http = context.HttpContext;
        HttpRequest request = http.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method)
            || HubPrincipal.Method(http.User) == AuthMethods.Token)
        {
            return next(context);
        }

        if (!string.Equals(request.Headers[HeaderName].ToString(), HeaderValue, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<object?>(HubApiResults.Problem(
                StatusCodes.Status403Forbidden,
                "csrf_header_missing",
                "Requests that change state must carry the header " + HeaderName + ": " + HeaderValue + "."));
        }

        string origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsSameOrigin(origin, request))
        {
            return ValueTask.FromResult<object?>(HubApiResults.Problem(
                StatusCodes.Status403Forbidden,
                "cross_origin",
                "The request comes from another origin."));
        }

        return next(context);
    }

    private static bool IsSameOrigin(string origin, HttpRequest request)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
