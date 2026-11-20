using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Hub.Ui.Library;
using QoffeeAutomat.Hub.Ui.Tunnel;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>Registers what the hub UI needs. The host registers the <see cref="HttpClient"/> (its base address is the base address of the page).</summary>
public static class HubServiceCollectionExtensions
{
    /// <summary>
    /// Adds the hub API, the hub session, the tunnel to agents as the <see cref="IAgentApi"/> of the component library, the links of its pages
    /// below an agent, the hub texts, and what the component library registers. Services that are registered already (a fake hub in a test) are kept.
    /// </summary>
    /// <param name="services">The service collection of the app.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddQoffeeAutomatHubUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IHubApi>(provider => new HubApiClient(provider.GetRequiredService<HttpClient>()));
        services.TryAddScoped<HubSessionState>();
        services.TryAddScoped<AgentSelection>();
        services.TryAddScoped<HubText>();
        services.TryAddSingleton<HubUiOptions>();

        // The pages of the component library open below the selected agent, and they reach it through the tunnel. Both are registered before
        // AddQoffeeAutomatUi, which only adds its own defaults (the links of the agent UI) when nothing is there.
        services.TryAddScoped<IAppLinks, AgentScopedLinks>();
        services.TryAddScoped(provider => new TunnelAgentApi(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<AgentSelection>(),
            provider.GetRequiredService<IHubApi>()));
        services.TryAddScoped(provider => new LibraryAgentApi(
            provider.GetRequiredService<IHubApi>(),
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<AgentSelection>()));

        // The pages of the component library ask for an IAgentApi when they are created. Below an agent that is the tunnel to the agent; on a page of
        // the library (AgentSelection.LibraryMode, set before such a page renders its editors) it is the library of the hub, so the same editors
        // edit hub documents. Both are stateless towards the page, so a component simply gets the one that fits the page it is created for.
        services.TryAddTransient<IAgentApi>(provider => provider.GetRequiredService<AgentSelection>().LibraryMode
            ? provider.GetRequiredService<LibraryAgentApi>()
            : provider.GetRequiredService<TunnelAgentApi>());

        services.AddQoffeeAutomatUi();
        return services;
    }
}
