using Microsoft.Extensions.DependencyInjection.Extensions;

namespace QoffeeAutomat.Hub.Fleet.Actions;

/// <summary>Registers the services of the fleet actions (T917).</summary>
public static class FleetActionServices
{
    /// <summary>
    /// Adds the fleet action service and its options. Existing registrations of the same services are kept, so tests can replace them first. The hub database, the agent registry,
    /// <see cref="TimeProvider"/> and the services of <c>AddHubSecurity</c>, <c>AddHubGateway</c> and <c>AddHubEnrollment</c> must be registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddHubFleetActions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new FleetActionOptions());
        services.TryAddSingleton<FleetActionService>();
        return services;
    }
}
