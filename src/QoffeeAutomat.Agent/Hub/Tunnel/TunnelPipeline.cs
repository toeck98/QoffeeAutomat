using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace QoffeeAutomat.Agent.Hub.Tunnel;

/// <summary>Limits and timings of the management tunnel on the agent (PLAN.md S20, T912). Tests shorten the timings through the service collection.</summary>
public sealed class TunnelOptions
{
    /// <summary>How long one tunnelled request may run, and how long the opening of an event stream may take. The hub gives up earlier (its own request timeout).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The most requests of one hub connection that run at the same time; further requests fail with <c>busy</c>.</summary>
    public int MaxConcurrentRequests { get; init; } = 16;

    /// <summary>The most event streams of one hub connection that are open at the same time; further requests fail with <c>busy</c>.</summary>
    public int MaxStreams { get; init; } = 8;

    /// <summary>The largest response body that is carried back, in bytes. It must fit into one frame (1 MiB) with the envelope; a larger response becomes 502 <c>tunnel_response_too_large</c>.</summary>
    public int MaxResponseBytes { get; init; } = 900 * 1024;

    /// <summary>The largest request body that is accepted from the hub, in bytes.</summary>
    public int MaxRequestBytes { get; init; } = 900 * 1024;

    /// <summary>The largest <c>data</c> of one event of a stream, in bytes; a larger event ends the stream.</summary>
    public int MaxEventBytes { get; init; } = 256 * 1024;

    /// <summary>How many bytes of an event stream wait between the endpoint and the frame queue. When the buffer is full the endpoint's writes wait: the back-pressure that keeps a slow hub from making the agent buffer without limit.</summary>
    public int StreamBufferBytes { get; init; } = 64 * 1024;

    /// <summary>How long the agent waits for room in the send queue of the hub link before it ends a stream.</summary>
    public TimeSpan StreamSendTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The most event frames per second and hub connection; the hub disconnects an agent that sends more than 200, so the pump stays below.</summary>
    public int MaxEventsPerSecond { get; init; } = 100;
}

/// <summary>
/// Holds the request pipeline of the agent so that the management tunnel can run a request through it in process (PLAN.md S20, T912). The pipeline is captured by a
/// start-up filter, which runs before routing is added: a tunnelled request therefore passes the same middleware, routing, authentication, authorization, endpoint filters
/// and endpoints as a request of the browser, and no rule of the management API can be bypassed.
/// </summary>
public sealed class TunnelPipeline
{
    private volatile RequestDelegate? _pipeline;

    /// <summary>The pipeline, or null before the host has built it.</summary>
    public RequestDelegate? Current => _pipeline;

    internal void Capture(RequestDelegate pipeline) => _pipeline = pipeline;
}

/// <summary>Captures the request pipeline for <see cref="TunnelPipeline"/>; it adds no middleware.</summary>
internal sealed class TunnelPipelineStartupFilter(TunnelPipeline pipeline) : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            _ = app.Use(inner =>
            {
                pipeline.Capture(inner);
                return inner;
            });
            next(app);
        };
    }
}
