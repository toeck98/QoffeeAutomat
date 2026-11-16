using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Link;

/// <summary>
/// Ties the QoffeeLink server to the life of the agent and of the computer (PLAN.md S07): a power event that
/// takes the computer away (sleep, hibernate, shutdown, restart) is announced to the clients with <c>bye</c>
/// before it happens, and stopping the agent says <c>bye</c> with the reason <c>service_stop</c>. Hosted
/// services stop in the reverse order of their registration, so this service stops before the web server and
/// the connections are closed in an orderly way instead of holding up the shutdown.
/// </summary>
public sealed class LinkLifecycleService : IHostedService
{
    private readonly LinkServer _server;
    private readonly IPowerEvents _power;

    /// <summary>Creates the service.</summary>
    /// <param name="server">The QoffeeLink server.</param>
    /// <param name="power">The power events of the platform.</param>
    public LinkLifecycleService(LinkServer server, IPowerEvents power)
    {
        _server = server;
        _power = power;
    }

    /// <summary>Maps a power event to the <c>bye</c> reason; null for events that take nothing away (resume).</summary>
    /// <param name="kind">The power event.</param>
    /// <returns>One of <see cref="ByeReasons"/>, or null.</returns>
    public static string? ByeReasonOf(PowerEventKind kind)
    {
        return kind switch
        {
            PowerEventKind.Sleep => ByeReasons.Sleep,
            PowerEventKind.Hibernate => ByeReasons.Hibernate,
            PowerEventKind.Shutdown => ByeReasons.Shutdown,
            PowerEventKind.Restart => ByeReasons.Restart,
            _ => null,
        };
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _power.PowerChanged += OnPowerChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _power.PowerChanged -= OnPowerChanged;
        await _server.StopAsync(ByeReasons.ServiceStop, cancellationToken).ConfigureAwait(false);
    }

    private void OnPowerChanged(object? sender, PowerEventArgs e)
    {
        string? reason = ByeReasonOf(e.Kind);
        if (reason is null)
        {
            return;
        }

        // The computer may suspend as soon as this handler returns, so the frames are written first; the wait is
        // bounded by LinkOptions.ByeFlushTimeout and a client that does not take the frame is dropped.
        try
        {
            _server.SayByeAsync(reason, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Nothing left to tell.
        }
    }
}

/// <summary>Registers the QoffeeLink server.</summary>
public static class LinkServices
{
    /// <summary>
    /// Adds the server and its lifecycle service. The command engine, metric engine, manifest service, event bus,
    /// pairing service and platform must be registered; the options keep a registration that exists already.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddQoffeeLink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new LinkOptions());
        services.TryAddSingleton<LinkServer>();
        services.AddHostedService<LinkLifecycleService>();
        return services;
    }
}
