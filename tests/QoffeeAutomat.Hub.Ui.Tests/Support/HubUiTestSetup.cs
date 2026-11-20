using Bunit;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Pages.History;
using QoffeeAutomat.Ui.Components.Pages.Link;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Hub.Ui.Tests.Support;

/// <summary>Registers the fakes and the services of the hub UI in a bUnit test context.</summary>
internal static class HubUiTestSetup
{
    /// <summary>Sets the context up: loose JavaScript, the fake hub, a stub for the HTTP calls of the tunnel, English texts and quick intervals.</summary>
    /// <param name="context">The bUnit context.</param>
    /// <param name="hub">The fake hub.</param>
    /// <param name="browser">The fake browser.</param>
    /// <param name="tunnel">The stub that answers what the tunnel asks of an agent; <c>null</c> answers everything with 404.</param>
    public static void Register(BunitContext context, FakeHubApi hub, FakeBrowserInterop browser, StubHandler? tunnel = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHubApi>(hub);
        context.Services.AddSingleton<IBrowserInterop>(browser);

        // The overview and the audit log read again on real timers; the defaults (seconds) must not fire inside a test.
        // The metric pages work with time ranges from the clock of the options: a fixed clock makes the queries of a test exact.
        context.Services.AddSingleton(new HubUiOptions { RefreshInterval = TimeSpan.FromHours(1), AuditPageSize = 2, Time = new FixedTimeProvider(Samples.Now) });

        // The editors of the component library run inside the library pages: no browser, so the code is edited in a text area, and the editor
        // validates on a timer that must not fire by itself during a test (the tests of the component library explain why).
        context.Services.AddSingleton(new CommandEditorOptions { UseMonaco = false, ValidationDelay = TimeSpan.FromHours(1) });

        // The pages of the component library that run inside the agent pages poll on real timers as well.
        context.Services.AddSingleton(new DashboardOptions { RefreshInterval = TimeSpan.FromHours(1), ReconnectDelay = TimeSpan.FromHours(1) });
        context.Services.AddSingleton(new LinkOptions { PairingPollInterval = TimeSpan.FromHours(1), ClientRefreshInterval = TimeSpan.FromHours(1) });
        context.Services.AddSingleton(new LogViewerOptions { FollowInterval = TimeSpan.FromHours(1) });

        StubHandler handler = tunnel ?? new StubHandler(_ => StubHandler.Json(System.Net.HttpStatusCode.NotFound, "{\"type\":\"about:blank\",\"title\":\"Not Found\",\"status\":404,\"code\":\"not_found\"}", "application/problem+json"));
        context.Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        context.Services.AddQoffeeAutomatHubUi();
        context.Services.GetRequiredService<Localizer>().SetLanguage("en");
    }

    /// <summary>Signs a user with the role in at the fake hub.</summary>
    /// <param name="hub">The fake hub.</param>
    /// <param name="role">One of <see cref="HubRoleNames"/>.</param>
    /// <returns>The same hub.</returns>
    public static FakeHubApi SignedIn(this FakeHubApi hub, string role)
    {
        ArgumentNullException.ThrowIfNull(hub);

        hub.Me = Samples.Session(role);
        hub.Setup = null;
        return hub;
    }
}
