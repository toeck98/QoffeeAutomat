using Microsoft.Extensions.DependencyInjection.Extensions;

using QoffeeAutomat.Hub.Metrics.Prometheus;

namespace QoffeeAutomat.Hub.Metrics;

/// <summary>Registers the metric collection of the hub (T910) and the Prometheus exporter (T911).</summary>
public static class MetricServices
{
    /// <summary>
    /// Adds the options (from the environment, see <see cref="HubMetricsOptions.FromEnvironment"/>), the SQLite store, the policy source and the collector, and starts the
    /// collector with the host. Existing registrations of the same services are kept, so tests can replace them first. The gateway (<c>AddHubGateway</c>), the hub
    /// database and <see cref="TimeProvider"/> must be registered, and so must the agent registry (<c>AddHubEnrollment</c>) for the endpoints.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddHubMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(_ => HubMetricsOptions.FromEnvironment(Environment.GetEnvironmentVariable));
        services.TryAddSingleton<IMetricPolicySource, GlobalMetricPolicySource>();
        services.TryAddSingleton<SqliteMetricStore>();
        services.TryAddSingleton<IMetricStore>(provider => provider.GetRequiredService<SqliteMetricStore>());
        services.TryAddSingleton<MetricCollector>();
        services.AddHostedService(provider => provider.GetRequiredService<MetricCollector>());
        services.TryAddSingleton<PrometheusExporter>();
        return services;
    }
}
