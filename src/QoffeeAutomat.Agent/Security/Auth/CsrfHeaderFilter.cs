using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>
/// Anti-forgery protection for the management API (PLAN.md S09 item 1): every request whose method can
/// change state must carry the header <see cref="HeaderName"/> with the value <see cref="HeaderValue"/>.
/// A browser cannot add a custom header to a cross-site request without a CORS preflight, and the agent
/// answers no preflight, so a foreign page cannot forge such a request even though the session cookie
/// would travel with it. If the request names an origin, it must be the origin of the agent itself. The
/// cookie is also <c>SameSite=Strict</c>. The filter is attached to the whole <c>/api/v1</c> group, so it
/// also covers sign-in and setup.
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

        HttpRequest request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        {
            return next(context);
        }

        if (!string.Equals(request.Headers[HeaderName].ToString(), HeaderValue, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<object?>(ApiResults.Problem(
                StatusCodes.Status403Forbidden,
                "csrf_header_missing",
                "Requests that change state must carry the header " + HeaderName + ": " + HeaderValue + "."));
        }

        string origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsSameOrigin(origin, request))
        {
            return ValueTask.FromResult<object?>(ApiResults.Problem(
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
