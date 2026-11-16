using Microsoft.Extensions.Hosting;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// Brings the engines to life with the host: the command registry follows configuration changes, the
/// platform event publisher turns session and power changes into events, and once the listeners are
/// up the event <c>agent_started</c> is published (PLAN.md T207, T217). The services themselves are
/// created here on first use, so a start-up failure (for example a database that cannot be opened)
/// stops the agent instead of surfacing on the first request.
/// </summary>
public sealed class EngineStartService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly CommandRegistry _registry;
    private readonly CommandEngine _engine;
    private readonly PlatformEventPublisher _platformEvents;
    private readonly IEventBus _events;
    private readonly MetricEngine _metrics;
    private readonly ManifestService _manifest;

    /// <summary>Creates the service.</summary>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="registry">The command registry.</param>
    /// <param name="engine">The command engine; resolving it opens the run history.</param>
    /// <param name="platformEvents">The publisher of session and power events.</param>
    /// <param name="events">The event bus.</param>
    /// <param name="metrics">The metric engine; its scheduler starts with the host and samples only for subscribers.</param>
    /// <param name="manifest">The manifest service; it starts watching the commands, metrics and settings.</param>
    public EngineStartService(
        IHostApplicationLifetime lifetime,
        CommandRegistry registry,
        CommandEngine engine,
        PlatformEventPublisher platformEvents,
        IEventBus events,
        MetricEngine metrics,
        ManifestService manifest)
    {
        _lifetime = lifetime;
        _registry = registry;
        _engine = engine;
        _platformEvents = platformEvents;
        _events = events;
        _metrics = metrics;
        _manifest = manifest;
    }

    /// <summary>The engine that this service started.</summary>
    public CommandEngine Engine => _engine;

    /// <summary>The publisher of platform events that this service started.</summary>
    public PlatformEventPublisher PlatformEvents => _platformEvents;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registry.Start();
        _metrics.Start();
        _manifest.Start();
        _lifetime.ApplicationStarted.Register(() => _ = PublishStartedAsync());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task PublishStartedAsync()
    {
        try
        {
            await _events.PublishAsync(
                EventTypes.AgentStarted,
                EventData.Create(("version", AgentVersion.Current)),
                _lifetime.ApplicationStopping);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // The agent was stopped before the event could be published; nothing to report.
        }
    }
}
