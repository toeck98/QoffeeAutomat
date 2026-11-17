using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Updates;

namespace QoffeeAutomat.Agent.Updates;

/// <summary>Registers the update check and the self-update (T704).</summary>
public static class UpdateServices
{
    /// <summary>
    /// Adds the checker (a hosted service and the <see cref="IUpdateStatusSource"/>), the updater and the built-in action
    /// <c>agent.update</c>. Call it after the platform registered its built-in actions: the action is added by wrapping them.
    /// Every registration that a test may want to replace is added with <c>TryAdd</c>, and the options with a factory that a later
    /// <c>AddSingleton</c> overrides. The self-update is supported only on the Windows platform; elsewhere the agent only reports its own
    /// version and makes no request to GitHub.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="platform">The selected platform.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddAgentUpdates(this IServiceCollection services, PlatformSelection platform)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(platform);

        services.TryAddSingleton(new UpdateOptions { Supported = platform.Kind == PlatformKind.Windows });
        services
            .AddHttpClient(UpdateOptions.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });

        services.TryAddSingleton<IReleaseSource>(provider => new GitHubReleaseClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(UpdateOptions.HttpClientName),
            provider.GetRequiredService<UpdateOptions>()));
        services.TryAddSingleton<IUpdateSettingsSource>(provider => new ConfigUpdateSettingsSource(provider.GetRequiredService<IConfigStore>()));

        // The Windows platform registers the real verifier before this runs; everywhere else nothing counts as signed.
        services.TryAddSingleton<IAuthenticodeVerifier, NoAuthenticodeVerifier>();
        services.TryAddSingleton<IByeAnnouncer>(provider => new LinkByeAnnouncer(provider));

        services.AddSingleton(provider => new UpdateChecker(
            provider.GetRequiredService<IReleaseSource>(),
            provider.GetRequiredService<IUpdateSettingsSource>(),
            provider.GetRequiredService<UpdateOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<UpdateChecker>>()));
        services.AddSingleton<IUpdateStatusSource>(provider => provider.GetRequiredService<UpdateChecker>());
        services.AddHostedService(provider => provider.GetRequiredService<UpdateChecker>());

        services.AddSingleton<IAgentUpdater>(provider => new AgentUpdater(
            provider.GetRequiredService<IReleaseSource>(),
            provider.GetRequiredService<IAuthenticodeVerifier>(),
            provider.GetRequiredService<IProcessLauncher>(),
            provider.GetRequiredService<IByeAnnouncer>(),
            provider.GetRequiredService<IAppPaths>(),
            provider.GetRequiredService<UpdateOptions>(),
            provider.GetRequiredService<ILogger<AgentUpdater>>(),
            provider.GetRequiredService<UpdateChecker>()));

        DecorateBuiltinActions(services);
        return services;
    }

    private static void DecorateBuiltinActions(IServiceCollection services)
    {
        ServiceDescriptor inner = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IBuiltinActionProvider) && !descriptor.IsKeyedService)
            ?? throw new InvalidOperationException("The built-in actions of the platform must be registered before the updates.");
        services.Remove(inner);
        services.AddSingleton<IBuiltinActionProvider>(provider => new UpdateBuiltinActions(
            CreateInner(provider, inner),
            provider.GetRequiredService<IAgentUpdater>(),
            provider.GetRequiredService<UpdateOptions>()));
    }

    private static IBuiltinActionProvider CreateInner(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IBuiltinActionProvider instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return (IBuiltinActionProvider)factory(provider);
        }

        return (IBuiltinActionProvider)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }
}
