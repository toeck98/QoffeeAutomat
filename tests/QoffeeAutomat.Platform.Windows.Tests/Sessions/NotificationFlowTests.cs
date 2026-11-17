using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.Platform.Windows.Tests.Notifications;
using QoffeeAutomat.SessionHost.Notifications;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// The way of a notification button press (PLAN.md S11, S16, T702): the presenter of the SessionHost reports it, the pipe carries
/// it to the agent, and the relay publishes the event <c>notification_action</c>. The toast itself is shown by Windows and checked
/// by hand (human_verify); everything else runs here through a real named pipe and a fake presenter.
/// </summary>
[Trait("Category", "Notifications")]
public sealed class NotificationFlowTests
{
    /// <summary>
    /// Presses until an event arrives: a press in the moment between the handshake of the agent's side and the one of the client's
    /// side finds no connection and is lost (and logged), which is the behaviour the SessionHost has on purpose.
    /// </summary>
    private static async Task<AgentEvent> PressUntilEventAsync(FakeToastPresenter presenter, EventSubscription events, string action, string? tag)
    {
        using var timeout = new CancellationTokenSource(TestWait.Patience);
        Task<AgentEvent> next = events.Events.ReadAsync(timeout.Token).AsTask();
        while (!next.IsCompleted)
        {
            presenter.Press(action, tag);
            await Task.WhenAny(next, Task.Delay(100, timeout.Token));
        }

        return await next;
    }

    [Fact]
    public async Task A_button_press_in_the_session_becomes_the_event_notification_action()
    {
        using var h = new SessionHarness();
        await h.StartAsync();
        using var relay = new NotificationActionRelay(h.Manager, h.Bus);
        await relay.StartAsync(CancellationToken.None);
        using EventSubscription events = h.Bus.Subscribe([EventTypes.NotificationAction]);
        var presenter = new FakeToastPresenter();
        NotificationCoordinator? coordinator = null;
        h.StartHost(beforeStart: client => coordinator = new NotificationCoordinator(client, presenter));
        using NotificationCoordinator running = coordinator!;

        AgentEvent published = await PressUntilEventAsync(presenter, events, "open", "door");

        Assert.Equal(EventTypes.NotificationAction, published.Type);
        Assert.Equal("open", published.Data!.Value.GetProperty("action").GetString());
        Assert.Equal("door", published.Data!.Value.GetProperty("tag").GetString());

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_press_of_a_notification_without_a_tag_has_no_tag_in_the_event()
    {
        using var h = new SessionHarness();
        await h.StartAsync();
        using var relay = new NotificationActionRelay(h.Manager, h.Bus);
        await relay.StartAsync(CancellationToken.None);
        using EventSubscription events = h.Bus.Subscribe([EventTypes.NotificationAction]);
        var presenter = new FakeToastPresenter();
        NotificationCoordinator? coordinator = null;
        h.StartHost(beforeStart: client => coordinator = new NotificationCoordinator(client, presenter));
        using NotificationCoordinator running = coordinator!;

        AgentEvent published = await PressUntilEventAsync(presenter, events, "ignore", tag: null);

        Assert.Equal("ignore", published.Data!.Value.GetProperty("action").GetString());
        Assert.False(published.Data!.Value.TryGetProperty("tag", out _));

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_action_that_is_not_a_plain_id_never_becomes_an_event()
    {
        using var h = new SessionHarness();
        await h.StartAsync();
        using var relay = new NotificationActionRelay(h.Manager, h.Bus);
        await relay.StartAsync(CancellationToken.None);
        using EventSubscription events = h.Bus.Subscribe([EventTypes.NotificationAction]);
        var presenter = new FakeToastPresenter();
        NotificationCoordinator? coordinator = null;
        h.StartHost(beforeStart: client => coordinator = new NotificationCoordinator(client, presenter));
        using NotificationCoordinator running = coordinator!;
        await TestWait.UntilAsync(() => h.Manager.GetConnections().Count == 1, "the connection of the session helper");

        // Free text is dropped by the relay; the next valid press is the first event that arrives.
        presenter.Press("free text with spaces", "tag");
        AgentEvent published = await PressUntilEventAsync(presenter, events, "valid_id", tag: null);

        Assert.Equal("valid_id", published.Data!.Value.GetProperty("action").GetString());

        await relay.StopAsync(CancellationToken.None);
    }
}
