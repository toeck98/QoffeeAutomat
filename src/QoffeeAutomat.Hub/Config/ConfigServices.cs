using Microsoft.Extensions.DependencyInjection.Extensions;

namespace QoffeeAutomat.Hub.Config;

/// <summary>Registers the configuration service of the hub (T907).</summary>
public static class ConfigServices
{
    /// <summary>Adds the services. Existing registrations of the same services are kept, so tests can replace them first.</summary>
    /// <param name="services">The service collection; the hub database, <see cref="Security.HubSigningKeyStore"/>, <see cref="TimeProvider"/> and the agent registry (<c>AddHubEnrollment</c>) must be registered.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddHubConfig(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IConfigAgentSource, RegistryConfigAgentSource>();
        services.TryAddSingleton<ConfigService>();
        return services;
    }
}
