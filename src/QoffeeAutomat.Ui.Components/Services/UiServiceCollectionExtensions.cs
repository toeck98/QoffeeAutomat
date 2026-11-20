using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Pages.History;
using QoffeeAutomat.Ui.Components.Pages.Link;
using QoffeeAutomat.Ui.Components.Pages.Modes;

namespace QoffeeAutomat.Ui.Components.Services;

/// <summary>Registers what the components need. The host registers <see cref="Api.IAgentApi"/> itself.</summary>
public static class UiServiceCollectionExtensions
{
    /// <summary>
    /// Adds MudBlazor, the localizer, the session state and the browser interop. Services that are registered already
    /// (a fake browser in a test, a different clock) are kept.
    /// </summary>
    /// <param name="services">The service collection of the app.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddQoffeeAutomatUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMudServices();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<Localizer>();
        services.TryAddScoped<SessionState>();
        services.TryAddScoped<IBrowserInterop, JsBrowserInterop>();
        services.TryAddScoped<IAppLinks, RootAppLinks>();
        services.TryAddSingleton<DashboardOptions>();
        services.TryAddSingleton<CommandEditorOptions>();
        services.TryAddSingleton<LinkOptions>();
        services.TryAddSingleton<LogViewerOptions>();
        services.TryAddSingleton<ModeOptions>();
        return services;
    }
}
