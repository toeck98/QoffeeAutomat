using System.Text;
using Microsoft.Extensions.FileProviders;
using QoffeeAutomat.Hub.Api;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>
/// Serves the hub UI (PLAN.md D-033, T915): the Blazor WebAssembly app is a set of static files that the hub hands out on its listeners.
/// A request for a client route (<c>/agents/pc-1</c>, <c>/login</c>) is answered with <c>index.html</c>, so that the app can show the page,
/// but never one below <c>/api</c>, <c>/hub</c>, <c>/metrics</c>, <c>/healthz</c> or <c>/openapi</c>: those belong to the hub and answer 404
/// when nothing is mapped there.
/// <para>
/// The app is served from the root of the site or below a path prefix. Behind Home Assistant ingress the Supervisor strips the prefix and
/// reports it in <c>X-Ingress-Path</c>; a reverse proxy that strips a prefix reports it in <c>X-Forwarded-Prefix</c>. The base address of the
/// page (<c>&lt;base href&gt;</c> in <c>index.html</c>) is rewritten to the prefix, so every relative address of the app (scripts, the API at
/// <c>api/v1/...</c>, the links of the pages) stays below it. The header is taken as text of a path, never as markup: only a path of
/// the characters of <see cref="IsSafeBasePath"/> is accepted.
/// </para>
/// </summary>
public static class HubUiHosting
{
    /// <summary>The page that starts the app.</summary>
    public const string IndexFile = "index.html";

    /// <summary>The header of Home Assistant ingress that carries the path prefix of the request.</summary>
    public const string IngressPathHeader = "X-Ingress-Path";

    /// <summary>The header of a reverse proxy that carries the path prefix that it removed.</summary>
    public const string ForwardedPrefixHeader = "X-Forwarded-Prefix";

    /// <summary>The element of <c>index.html</c> that names the base address; the host replaces it.</summary>
    public const string BaseElement = "<base href=\"/\" />";

    private const string WebRootDirectory = "wwwroot";

    /// <summary>The path prefixes that belong to the hub and are never answered with the UI.</summary>
    public static readonly IReadOnlyList<string> ReservedPrefixes =
    [
        HubApi.BasePath,
        "/hub",
        "/metrics",
        HubHost.HealthPath,
        "/openapi",
    ];

    /// <summary>Tells whether a path belongs to the hub and not to the UI.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>True for <c>/api/...</c>, <c>/hub/...</c>, <c>/metrics</c>, <c>/healthz</c> and <c>/openapi/...</c>.</returns>
    public static bool IsReserved(PathString path)
    {
        foreach (string prefix in ReservedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tells whether a text is acceptable as a path prefix: it starts with a slash, has no empty or dot segment and consists of letters,
    /// digits and <c>. _ ~ -</c>, so it cannot leave the attribute it is written into and cannot point to another site.
    /// </summary>
    /// <param name="path">The candidate, for example <c>/api/hassio_ingress/Zk3x</c>.</param>
    /// <returns>True when the text can be used as a base path.</returns>
    public static bool IsSafeBasePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Length > 256)
        {
            return false;
        }

        string trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return true;
        }

        foreach (string segment in trimmed[1..].Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }

            foreach (char character in segment)
            {
                bool allowed = char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '~' or '-';
                if (!allowed)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Reads the path prefix of a request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The prefix with a leading and a trailing slash (<c>/api/hassio_ingress/Zk3x/</c>), or <c>/</c> when there is none or the header is not a safe path.</returns>
    public static string ResolveBasePath(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (string header in new[] { IngressPathHeader, ForwardedPrefixHeader })
        {
            string? value = request.Headers[header].FirstOrDefault();
            if (IsSafeBasePath(value))
            {
                string trimmed = value!.TrimEnd('/');
                return trimmed.Length == 0 ? "/" : trimmed + "/";
            }
        }

        return "/";
    }

    /// <summary>Writes the base address into the page.</summary>
    /// <param name="indexHtml">The text of <c>index.html</c>.</param>
    /// <param name="basePath">The base path from <see cref="ResolveBasePath"/>.</param>
    /// <returns>The page; unchanged when the base is the root or the page has no base element.</returns>
    public static string RewriteBase(string indexHtml, string basePath)
    {
        ArgumentNullException.ThrowIfNull(indexHtml);
        ArgumentNullException.ThrowIfNull(basePath);

        return basePath == "/"
            ? indexHtml
            : indexHtml.Replace(BaseElement, "<base href=\"" + basePath + "\" />", StringComparison.Ordinal);
    }

    /// <summary>Serves the static files of the UI. Call it before the audit and authentication middleware, so that files need neither.</summary>
    /// <param name="app">The application.</param>
    public static void UseFiles(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        UseApplicationDirectoryWhenNeeded(app.Environment);
        app.UseBlazorFrameworkFiles();
        app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => Harden(context.Context.Response, framed: false) });
    }

    /// <summary>Maps the fallback that answers client routes with <c>index.html</c> (single-page app).</summary>
    /// <param name="routes">The route builder.</param>
    public static void MapFallback(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // nonfile: an address with a file extension (a missing script or image) is a 404, not the app. MapFallback gives the endpoint the lowest
        // order; limiting it to GET and HEAD keeps "405 Method Not Allowed" for the other methods.
        routes.MapFallback("{*path:nonfile}", ServeIndexAsync)
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]));
    }

    private static async Task ServeIndexAsync(HttpContext context, IWebHostEnvironment environment)
    {
        if (IsReserved(context.Request.Path))
        {
            await HubApiResults.NotFound("not_found", "There is nothing at this address.").ExecuteAsync(context);
            return;
        }

        IFileInfo index = environment.WebRootFileProvider.GetFileInfo(IndexFile);
        if (!index.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string basePath = ResolveBasePath(context.Request);
        HttpResponse response = context.Response;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Vary = IngressPathHeader + ", " + ForwardedPrefixHeader;
        Harden(response, framed: basePath != "/");
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        string text;
        await using (Stream stream = index.CreateReadStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = await reader.ReadToEndAsync(context.RequestAborted);
        }

        await response.WriteAsync(RewriteBase(text, basePath), Encoding.UTF8, context.RequestAborted);
    }

    /// <summary>
    /// When the web root next to the current directory has no UI (the hub was started from another directory), the <c>wwwroot</c> directory next
    /// to the executable is used instead.
    /// </summary>
    private static void UseApplicationDirectoryWhenNeeded(IWebHostEnvironment environment)
    {
        if (environment.WebRootFileProvider.GetFileInfo(IndexFile).Exists)
        {
            return;
        }

        string directory = Path.Combine(AppContext.BaseDirectory, WebRootDirectory);
        if (File.Exists(Path.Combine(directory, IndexFile)))
        {
            environment.WebRootPath = directory;
            environment.WebRootFileProvider = new PhysicalFileProvider(directory);
        }
    }

    /// <summary>
    /// Headers for everything the UI serves: no content sniffing, no referrer, and no framing (clickjacking) except for the pages that Home
    /// Assistant ingress embeds, which may be framed by the site that serves them.
    /// </summary>
    private static void Harden(HttpResponse response, bool framed)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.ContentSecurityPolicy = framed ? "frame-ancestors 'self'" : "frame-ancestors 'none'";
    }
}
