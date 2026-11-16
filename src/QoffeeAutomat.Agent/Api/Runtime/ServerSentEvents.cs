using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace QoffeeAutomat.Agent.Api.Runtime;

/// <summary>
/// Writes Server-Sent Events (PLAN.md S08: live run output and live metric values). Every event is one
/// <c>event:</c> line and one <c>data:</c> line with compact JSON, followed by a blank line, and is flushed at once
/// so that the browser sees it while the run is still going.
/// </summary>
public static class ServerSentEvents
{
    /// <summary>The media type of an event stream.</summary>
    public const string ContentType = "text/event-stream";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Sends the response headers of an event stream and flushes them.</summary>
    /// <param name="context">The request context.</param>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>A task that completes when the headers are sent.</returns>
    public static async Task StartAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpResponse response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await response.StartAsync(cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one event.</summary>
    /// <param name="response">The response of an event stream.</param>
    /// <param name="name">The event name.</param>
    /// <param name="json">The data: one line of JSON.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the event is flushed.</returns>
    public static async Task WriteAsync(HttpResponse response, string name, string json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(json);

        byte[] bytes = Utf8.GetBytes("event: " + name + "\ndata: " + json + "\n\n");
        await response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
