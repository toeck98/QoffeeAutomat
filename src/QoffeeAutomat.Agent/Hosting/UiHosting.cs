using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Agent.Security.Auth;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// Serves the web UI (PLAN.md D-015, D-018, T401): the Blazor WebAssembly app is a set of static files that the agent
/// hands out on both listeners. On the HTTPS listener the files are gated by <see cref="RemoteAccessGate"/> exactly like
/// the API, so they are served to other PCs only while "remote UI" is on. A request for a client route (<c>/login</c>,
/// <c>/commands/edit</c>) is answered with <c>index.html</c> so that the app can show the page, but never one below
/// <c>/api</c>, <c>/link</c>, <c>/healthz</c>, <c>/openapi</c> or <c>/local</c>: those stay with their endpoints and answer
/// 404 when nothing is mapped there.
/// </summary>
public static class UiHosting
{
    /// <summary>The page that starts the app.</summary>
    public const string IndexFile = "index.html";

    private const string WebRootDirectory = "wwwroot";

    /// <summary>
    /// The path prefixes that belong to the agent and are never answered with the UI. <c>/healthz</c>, the link and the
    /// local channel are also the paths that <see cref="RemoteAccessGate"/> leaves open to other PCs.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedPrefixes = ["/api", "/openapi", "/link", AgentHost.HealthPath, "/local"];

    /// <summary>Tells whether a path belongs to the agent and not to the UI.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>True for <c>/api/...</c>, <c>/link/...</c>, <c>/healthz</c>, <c>/openapi/...</c> and <c>/local/...</c>.</returns>
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
    /// Serves the static files of the UI. Call it after <see cref="RemoteAccessGate"/>, so that the files are gated too.
    /// </summary>
    /// <param name="app">The application.</param>
    public static void UseFiles(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        UseApplicationDirectoryWhenNeeded(app.Environment);
        app.UseBlazorFrameworkFiles();
        app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => Harden(context.Context.Response) });
    }

    /// <summary>Maps the fallback that answers client routes with <c>index.html</c> (single-page app).</summary>
    /// <param name="routes">The route builder.</param>
    public static void MapFallback(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // nonfile: an address with a file extension (a missing script or image) is a 404, not the app. MapFallback gives the
        // endpoint the lowest order; limiting it to GET and HEAD keeps "405 Method Not Allowed" for the other methods.
        routes.MapFallback("{*path:nonfile}", ServeIndexAsync)
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]));
    }

    private static async Task ServeIndexAsync(HttpContext context, IWebHostEnvironment environment)
    {
        if (IsReserved(context.Request.Path))
        {
            await ApiResults.Problem(StatusCodes.Status404NotFound, "not_found", "There is nothing at this address.").ExecuteAsync(context);
            return;
        }

        IFileInfo index = environment.WebRootFileProvider.GetFileInfo(IndexFile);
        if (!index.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        HttpResponse response = context.Response;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        Harden(response);
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        await response.SendFileAsync(index, context.RequestAborted);
    }

    /// <summary>
    /// When the web root next to the current directory has no UI (the agent was started from another directory, as
    /// <c>qoffeeautomat run</c> usually is), the <c>wwwroot</c> directory next to the executable is used instead.
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

    /// <summary>Headers for everything the UI serves: no content sniffing, no framing (clickjacking), no referrer.</summary>
    private static void Harden(HttpResponse response)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
    }
}
