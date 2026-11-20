using Microsoft.Extensions.DependencyInjection.Extensions;

namespace QoffeeAutomat.Hub.Config.Rollout;

/// <summary>Registers the configuration roll-out of the hub (T909).</summary>
public static class RolloutServices
{
    /// <summary>
    /// Adds <see cref="ConfigRolloutService"/> and starts it with the host. The gateway (<c>AddHubGateway</c>), the configuration service (<c>AddHubConfig</c>) and the
    /// hub database must be registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddHubRollout(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ConfigRolloutService>();
        services.AddHostedService(provider => provider.GetRequiredService<ConfigRolloutService>());
        return services;
    }
}
