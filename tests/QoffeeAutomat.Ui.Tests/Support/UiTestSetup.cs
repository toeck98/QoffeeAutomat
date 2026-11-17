using Bunit;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Pages.History;
using QoffeeAutomat.Ui.Components.Pages.Link;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Tests.Support;

/// <summary>Registers the fakes and the services of the component library in a bUnit test context.</summary>
internal static class UiTestSetup
{
    /// <summary>Sets the context up: loose JavaScript, the fakes, English texts and quick intervals.</summary>
    public static void Register(BunitContext context, IAgentApi api, FakeBrowserInterop browser, Action<DashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IAgentApi>(api);
        context.Services.AddSingleton<IBrowserInterop>(browser);

        // The refresh and the reconnect are real timers; the defaults (seconds) would never fire inside a test.
        var options = new DashboardOptions
        {
            RefreshInterval = TimeSpan.FromHours(1),
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
        };
        configure?.Invoke(options);
        context.Services.AddSingleton(options);

        // The pairing countdown and the client list refresh on real timers as well.
        context.Services.AddSingleton(new LinkOptions
        {
            PairingPollInterval = TimeSpan.FromMilliseconds(20),
            ClientRefreshInterval = TimeSpan.FromHours(1),
        });

        // Follow mode of the log viewer polls on a real timer.
        context.Services.AddSingleton(new LogViewerOptions { FollowInterval = TimeSpan.FromMilliseconds(20) });

        // No browser: the code is edited in a text area. The editor validates the draft on a background timer that re-renders the
        // page from another thread; a short delay would fire between a test's Find and the event it triggers and swallow that
        // event (the handler ids of the re-rendered controls are replaced). So validation never fires by itself here; the
        // tests that are about it set a short delay and then wait for the result.
        context.Services.AddSingleton(new CommandEditorOptions { UseMonaco = false, ValidationDelay = TimeSpan.FromHours(1) });

        context.Services.AddQoffeeAutomatUi();
        context.Services.GetRequiredService<Localizer>().SetLanguage("en");
    }

    /// <summary>The agent has a password and the browser has a session.</summary>
    public static FakeAgentApi SignedIn(this FakeAgentApi api)
    {
        api.Me = new QoffeeAutomat.Contracts.Api.AuthMeResponse(true, "administrator");
        api.Setup = null;
        return api;
    }
}
