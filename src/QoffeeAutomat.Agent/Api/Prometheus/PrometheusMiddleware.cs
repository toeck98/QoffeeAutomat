using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Api.Prometheus;

/// <summary>Source-generated log messages of the Prometheus endpoint. No message contains a token.</summary>
internal static partial class PrometheusLog
{
    [LoggerMessage(EventId = 400, Level = LogLevel.Warning, Message = "A request for the Prometheus endpoint was refused: {Reason}.")]
    public static partial void Refused(ILogger logger, string reason);

    [LoggerMessage(EventId = 401, Level = LogLevel.Warning, Message = "The Prometheus endpoint cannot check its token: {Reason}")]
    public static partial void TokenUnavailable(ILogger logger, string reason);
}

/// <summary>
/// The optional Prometheus endpoint <c>GET /metrics</c> on the HTTPS listener (PLAN.md S08, S09, T709). It is off by default. While it is
/// on, a request needs the read-only token (<see cref="PrometheusTokenStore"/>) as <c>Authorization: Bearer</c>, whatever the address it comes
/// from, and is answered with the metrics of <see cref="PrometheusExporter"/>; a request without or with a wrong token is answered with 401,
/// and while the endpoint is off with 404. It sits in front of the remote-access gate, because the export is meant for another PC, and
/// it does nothing else: no cookie is read and no other path is touched.
/// <para>
/// <c>/metrics</c> is also a page of the web UI. A request that asks for HTML and carries no token (a browser that opens the page) is
/// passed on, so that the UI keeps working on the HTTPS listener while remote UI is on; a scraper asks for plain text. The loopback listener
/// never serves the export, only the UI.
/// </para>
/// </summary>
public sealed class PrometheusMiddleware
{
    /// <summary>The path of the endpoint.</summary>
    public const string Path = "/metrics";

    private readonly RequestDelegate _next;
    private readonly ILogger<PrometheusMiddleware> _logger;

    /// <summary>
    /// Creates the middleware. The exporter and the token store are taken from the request services when an export request arrives, so
    /// that the pipeline does not build the metric engine before the configuration is loaded, and no other request pays for them.
    /// </summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="logger">The logger.</param>
    public PrometheusMiddleware(RequestDelegate next, ILogger<PrometheusMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>Handles a request.</summary>
    /// <param name="context">The request context.</param>
    /// <returns>A task that completes when the request is handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpRequest request = context.Request;
        if (!IsExportRequest(request) || AsksForThePage(request))
        {
            await _next(context);
            return;
        }

        PrometheusExporter exporter = context.RequestServices.GetRequiredService<PrometheusExporter>();
        PrometheusTokenStore tokens = context.RequestServices.GetRequiredService<PrometheusTokenStore>();

        if (!exporter.IsEnabled)
        {
            await ApiResults.Problem(StatusCodes.Status404NotFound, "prometheus_disabled", "The Prometheus endpoint is not enabled.").ExecuteAsync(context);
            return;
        }

        bool allowed;
        try
        {
            allowed = await tokens.VerifyAsync(BearerToken(request), context.RequestAborted);
        }
        catch (SecretStoreException exception)
        {
            PrometheusLog.TokenUnavailable(_logger, exception.Message);
            await ApiResults.Problem(StatusCodes.Status500InternalServerError, "secret_store_unavailable", "The token of the endpoint cannot be checked.").ExecuteAsync(context);
            return;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        if (!allowed)
        {
            PrometheusLog.Refused(_logger, request.Headers.Authorization.Count == 0 ? "no token" : "wrong token");
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"QoffeeAutomat metrics\"";
            await ApiResults.Problem(StatusCodes.Status401Unauthorized, "unauthorized", "A valid Prometheus token is required.").ExecuteAsync(context);
            return;
        }

        string body;
        try
        {
            body = await exporter.ScrapeAsync(context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = PrometheusFormat.ContentType;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(body, context.RequestAborted);
    }

    /// <summary><c>GET /metrics</c> on the HTTPS listener.</summary>
    private static bool IsExportRequest(HttpRequest request)
    {
        return request.IsHttps
            && HttpMethods.IsGet(request.Method)
            && request.Path.Equals(Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A browser that opens the page of the UI: it asks for HTML and has no token. A scraper never asks for HTML.</summary>
    private static bool AsksForThePage(HttpRequest request)
    {
        return request.Headers.Authorization.Count == 0
            && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BearerToken(HttpRequest request)
    {
        return AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out AuthenticationHeaderValue? header)
            && string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            ? header.Parameter
            : null;
    }
}
