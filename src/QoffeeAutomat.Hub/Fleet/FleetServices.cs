using Microsoft.Extensions.DependencyInjection.Extensions;

namespace QoffeeAutomat.Hub.Fleet;

/// <summary>Registers the services of the fleet link (T913).</summary>
public static class FleetServices
{
    /// <summary>
    /// Adds the fleet token service, the pairing service, the fleet service and the lifecycle service that closes the fleet links when the hub stops. Existing registrations of the
    /// same services are kept, so tests can replace them first. The hub database, the hub identity, <see cref="TimeProvider"/> and the services of <c>AddHubSecurity</c>,
    /// <c>AddHubGateway</c> and <c>AddHubEnrollment</c> must be registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddHubFleet(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new FleetOptions());
        services.TryAddSingleton<FleetTokenService>();
        services.TryAddSingleton<FleetPairingService>();
        services.TryAddSingleton<FleetService>();
        services.AddHostedService<FleetLifecycleService>();
        return services;
    }
}
