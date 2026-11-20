using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Hub.Agents;

namespace QoffeeAutomat.Hub.Enrollment;

/// <summary>Registers the services of enrollment and the agent registry (T904).</summary>
public static class EnrollmentServices
{
    /// <summary>Adds the services. Existing registrations of the same services are kept, so tests can replace them first.</summary>
    /// <param name="services">The service collection; the hub database, the hub identity and <see cref="TimeProvider"/> must be registered, and <c>AddHubSecurity</c> must have been called.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddHubEnrollment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new HubEnrollmentOptions());
        services.TryAddSingleton<AgentRegistry>();
        services.TryAddSingleton<EnrollmentTokenService>();
        services.TryAddSingleton<EnrollmentService>();

        // The agent gateway (T906) replaces these two.
        services.TryAddSingleton<IAgentLinkNotifier, OfflineAgentLinkNotifier>();
        services.TryAddSingleton<IAgentLinkHandler, UnavailableAgentLinkHandler>();
        return services;
    }
}
