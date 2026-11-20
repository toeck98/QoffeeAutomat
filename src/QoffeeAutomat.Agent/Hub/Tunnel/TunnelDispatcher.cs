using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Agent.Hub.Tunnel;

/// <summary>One call of the management API that the hub makes through the tunnel.</summary>
/// <param name="Method">The HTTP method, upper case.</param>
/// <param name="Path">The path, starting with <c>/api/v1/</c>, already checked by <see cref="QoffeeAutomat.Contracts.Hub.TunnelAccess"/>.</param>
/// <param name="Query">The decoded query parameters; null for none.</param>
/// <param name="Body">The request body as JSON; null for none.</param>
/// <param name="Identity">The hub user and role.</param>
public sealed record TunnelCall(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string>? Query,
    JsonElement? Body,
    TunnelIdentity Identity);

/// <summary>What a tunnelled request answered.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The body as JSON; null when the response has none.</param>
public sealed record TunnelResult(int Status, JsonElement? Body);

/// <summary>The outcome of opening an event stream: either the stream, or the status and problem that the endpoint answered instead.</summary>
/// <param name="Status">The HTTP status of the endpoint's answer.</param>
/// <param name="Problem">The problem body when the stream did not open; null otherwise.</param>
/// <param name="Stream">The open stream; null when it did not open.</param>
public sealed record TunnelStreamOpening(int Status, JsonElement? Problem, TunnelEventReader? Stream);

/// <summary>
/// An event stream of the management API that runs in process: the endpoint writes Server-Sent Events into a bounded pipe, and the tunnel reads them from it. When the
/// pipe is full the endpoint's writes wait, so a hub that reads slowly slows the endpoint down instead of making the agent buffer. Disposing the stream aborts the request.
/// </summary>
public sealed class TunnelEventReader : IAsyncDisposable
{
    private static readonly TimeSpan EndWait = TimeSpan.FromSeconds(5);

    private readonly PipeReader _reader;
    private readonly Task _run;
    private readonly CancellationTokenSource _aborted;
    private readonly AsyncServiceScope _scope;
    private readonly int _maxEventBytes;
    private int _disposed;

    internal TunnelEventReader(PipeReader reader, Task run, CancellationTokenSource aborted, AsyncServiceScope scope, int maxEventBytes)
    {
        _reader = reader;
        _run = run;
        _aborted = aborted;
        _scope = scope;
        _maxEventBytes = maxEventBytes;
    }

    /// <summary>The events of the stream until the endpoint ends.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The events.</returns>
    /// <exception cref="InvalidDataException">An event is larger than <see cref="TunnelOptions.MaxEventBytes"/>.</exception>
    internal IAsyncEnumerable<SseEvent> ReadAsync(CancellationToken cancellationToken) => SseReader.ReadAsync(_reader, _maxEventBytes, cancellationToken);

    /// <summary>Aborts the request and releases what it held.</summary>
    /// <returns>A task that completes when the endpoint has ended.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _aborted.CancelAsync().ConfigureAwait(false);

        // Completing the reader releases an endpoint that waits for room in the pipe.
        await _reader.CompleteAsync().ConfigureAwait(false);
        try
        {
            await _run.WaitAsync(EndWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The endpoint ignores the abort; it ends on its own, and the scope is released with the garbage.
        }

        await _scope.DisposeAsync().ConfigureAwait(false);
        _aborted.Dispose();
    }
}

/// <summary>
/// Runs the requests of the hub through the agent's own request pipeline, in process (PLAN.md S20, T912): the same routing, authentication, authorization, filters and
/// endpoints as for the browser, with a principal built from the hub user and role. Nothing is bypassed: the local-configuration lock and the protection of hub documents
/// are filters of the endpoints, and the role rules were applied before (<see cref="QoffeeAutomat.Contracts.Hub.TunnelAccess"/>). Failures of the call itself (time out,
/// a response that is too large or not JSON) become problem responses with the codes <c>timeout</c>, <c>tunnel_response_too_large</c> and
/// <c>tunnel_unsupported_media</c>.
/// </summary>
public sealed class TunnelDispatcher
{
    private const string JsonMedia = "application/json";
    private const string ProblemMedia = "application/problem+json";
    private const string EventStreamMedia = "text/event-stream";

    private readonly TunnelPipeline _pipeline;
    private readonly IServiceScopeFactory _scopes;
    private readonly TunnelOptions _options;
    private readonly ILogger<TunnelDispatcher> _logger;

    /// <summary>Creates the dispatcher.</summary>
    /// <param name="pipeline">The captured request pipeline.</param>
    /// <param name="scopes">Creates the service scope of a request.</param>
    /// <param name="options">The limits.</param>
    /// <param name="logger">The logger.</param>
    public TunnelDispatcher(TunnelPipeline pipeline, IServiceScopeFactory scopes, TunnelOptions options, ILogger<TunnelDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _pipeline = pipeline;
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    /// <summary>Runs one request and collects its answer.</summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Fires when the hub link ends; the request is aborted and the exception propagates.</param>
    /// <returns>The status and body. A failure of the call becomes a problem response, not an exception.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public async Task<TunnelResult> SendAsync(TunnelCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        RequestDelegate? pipeline = _pipeline.Current;
        if (pipeline is null)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "tunnel_unavailable", "The agent has not finished starting.");
        }

        byte[]? body = call.Body is { } element ? Encoding.UTF8.GetBytes(element.GetRawText()) : null;
        if (body is not null && body.Length > _options.MaxRequestBytes)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "The request body is too large for the management tunnel.");
        }

        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        aborted.CancelAfter(_options.RequestTimeout);
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        using var buffer = new BoundedBufferStream(_options.MaxResponseBytes);
        var feature = new TunnelResponseBodyFeature(buffer);
        DefaultHttpContext context = CreateContext(call, body, feature, scope.ServiceProvider, aborted, JsonMedia);
        try
        {
            await pipeline(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(StatusCodes.Status504GatewayTimeout, "timeout", "The request took too long on the agent.");
        }
        catch (IOException) when (buffer.Overflowed)
        {
            return Problem(StatusCodes.Status502BadGateway, "tunnel_response_too_large", "The response is larger than the management tunnel carries.");
        }
#pragma warning disable CA1031 // A failing endpoint must not take the hub link down; it answers 500 like it would to the browser.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            HubLog.TunnelFault(_logger, call.Method, call.Path, exception.GetType().Name);
            return Problem(StatusCodes.Status500InternalServerError, "internal", "The agent could not handle the request.");
        }

        return ToResult(context, buffer.Written);
    }

    /// <summary>Opens an event stream (a GET of a Server-Sent-Events endpoint) and waits until the endpoint has begun to answer.</summary>
    /// <param name="call">The call; its method must be GET.</param>
    /// <param name="cancellationToken">Fires when the hub link ends.</param>
    /// <returns>The open stream, or the answer of the endpoint when it is not a stream (an error status, for example).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public async Task<TunnelStreamOpening> OpenStreamAsync(TunnelCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        RequestDelegate? pipeline = _pipeline.Current;
        if (pipeline is null)
        {
            return Failed(Problem(StatusCodes.Status503ServiceUnavailable, "tunnel_unavailable", "The agent has not finished starting."));
        }

        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: _options.StreamBufferBytes,
            resumeWriterThreshold: _options.StreamBufferBytes / 2,
            useSynchronizationContext: false));
        CancellationTokenSource aborted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        AsyncServiceScope scope = _scopes.CreateAsyncScope();
        var feature = new TunnelResponseBodyFeature(pipe.Writer.AsStream(leaveOpen: true));
        DefaultHttpContext context = CreateContext(call, null, feature, scope.ServiceProvider, aborted, EventStreamMedia);
        Task run = Task.Run(() => RunStreamAsync(pipeline, context, pipe.Writer, call), CancellationToken.None);
        var stream = new TunnelEventReader(pipe.Reader, run, aborted, scope, _options.MaxEventBytes);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.RequestTimeout);
        Task timeout = Task.Delay(Timeout.InfiniteTimeSpan, limit.Token);
        Task first = await Task.WhenAny(feature.Started, run, timeout).ConfigureAwait(false);
        await limit.CancelAsync().ConfigureAwait(false);
        if (ReferenceEquals(first, timeout))
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Failed(Problem(StatusCodes.Status504GatewayTimeout, "timeout", "The stream did not open in time."));
        }

        int status = context.Response.StatusCode;
        string? media = MediaTypeOf(context.Response.ContentType);
        if (status == StatusCodes.Status200OK && string.Equals(media, EventStreamMedia, StringComparison.OrdinalIgnoreCase))
        {
            return new TunnelStreamOpening(status, null, stream);
        }

        // Not a stream: the endpoint refused (a problem response) or is no event endpoint at all. Its answer is small; read it, then let go.
        byte[] answer;
        try
        {
            answer = await ReadAllAsync(pipe.Reader, _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        if (status < StatusCodes.Status400BadRequest)
        {
            return Failed(Problem(StatusCodes.Status400BadRequest, "bad_request", "This endpoint does not answer with an event stream."));
        }

        TunnelResult result = ToResult(context, answer);
        return new TunnelStreamOpening(result.Status, result.Body, null);
    }

    private async Task RunStreamAsync(RequestDelegate pipeline, DefaultHttpContext context, PipeWriter writer, TunnelCall call)
    {
        try
        {
            await pipeline(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // The stream was closed, or the reader went away.
        }
#pragma warning disable CA1031 // The endpoint is the agent's own code; its failure ends the stream and is logged.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            HubLog.TunnelFault(_logger, call.Method, call.Path, exception.GetType().Name);
        }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadAllAsync(PipeReader reader, int limit, CancellationToken cancellationToken)
    {
        using var all = new MemoryStream();
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
            {
                if (all.Length + segment.Length > limit)
                {
                    reader.AdvanceTo(result.Buffer.End);
                    return all.ToArray();
                }

                all.Write(segment.Span);
            }

            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                return all.ToArray();
            }
        }
    }

    private static DefaultHttpContext CreateContext(
        TunnelCall call,
        byte[]? body,
        TunnelResponseBodyFeature responseBody,
        IServiceProvider services,
        CancellationTokenSource aborted,
        string accept)
    {
        string query = call.Query is { Count: > 0 } parameters
            ? QueryString.Create(parameters.Select(parameter => new KeyValuePair<string, string?>(parameter.Key, parameter.Value))).Value ?? string.Empty
            : string.Empty;
        var request = new HttpRequestFeature
        {
            Protocol = "HTTP/1.1",
            Scheme = "http",
            Method = call.Method,
            PathBase = string.Empty,
            Path = call.Path,
            QueryString = query,
            RawTarget = call.Path + query,
            Body = body is null ? Stream.Null : new MemoryStream(body, writable: false),
        };
        request.Headers.Host = "localhost";
        request.Headers.Accept = accept;
        if (!string.Equals(call.Method, "GET", StringComparison.Ordinal))
        {
            // The anti-forgery header protects the browser's cookie session; the tunnel carries no cookie, but the group's filter is part of the API and stays in force.
            request.Headers[ApiHeaders.CsrfName] = ApiHeaders.CsrfValue;
        }

        if (body is not null)
        {
            request.Headers.ContentType = JsonMedia;
            request.Headers.ContentLength = body.Length;
        }

        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(request);
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        features.Set<IHttpResponseBodyFeature>(responseBody);
        features.Set<IHttpRequestLifetimeFeature>(new TunnelLifetimeFeature(aborted.Cancel, aborted.Token));
        var context = new DefaultHttpContext(features) { RequestServices = services };
        context.Items[TunnelIdentity.ItemKey] = call.Identity;
        return context;
    }

    private static TunnelResult ToResult(HttpContext context, ReadOnlyMemory<byte> bytes)
    {
        int status = context.Response.StatusCode;
        if (status is < 100 or > 599)
        {
            return Problem(StatusCodes.Status500InternalServerError, "internal", "The agent answered with an invalid status.");
        }

        if (status is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified || bytes.IsEmpty)
        {
            return new TunnelResult(status, null);
        }

        string? media = MediaTypeOf(context.Response.ContentType);
        if (!(string.Equals(media, JsonMedia, StringComparison.OrdinalIgnoreCase) || string.Equals(media, ProblemMedia, StringComparison.OrdinalIgnoreCase)))
        {
            return Problem(
                StatusCodes.Status415UnsupportedMediaType,
                "tunnel_unsupported_media",
                "The endpoint answers with " + (media ?? "an unknown media type") + ", which the management tunnel cannot carry.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            return new TunnelResult(status, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Problem(StatusCodes.Status502BadGateway, "internal", "The agent answered with a body that is not valid JSON.");
        }
    }

    private static string? MediaTypeOf(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        int semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        return (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
    }

    private static TunnelStreamOpening Failed(TunnelResult result) => new(result.Status, result.Body, null);

    /// <summary>Builds the problem response of a failure of the tunnel itself, in the shape of the management API.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="code">The code.</param>
    /// <param name="detail">What went wrong.</param>
    /// <returns>The result.</returns>
    internal static TunnelResult Problem(int status, string code, string detail)
    {
        var problem = new ApiProblem("about:blank", ReasonPhrases.GetReasonPhrase(status), status, detail, code);
        return new TunnelResult(status, JsonSerializer.SerializeToElement(problem, ApiJsonContext.Default.ApiProblem));
    }
}
