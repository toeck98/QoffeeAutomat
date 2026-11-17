using System.Globalization;
using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Link;

/// <summary>
/// The pairing dialog's content. It asks the agent for a one-time code, counts down to its expiry, and keeps asking the agent
/// whether the code is still there: a code that vanishes before it expired was used by a client (or cancelled elsewhere), and
/// a client that is new in the list since the code was made means that a client has paired. The panel cancels a code that
/// is still valid when it is closed or left.
/// </summary>
public partial class PairingPanel
{
    private PairingPhase _phase = PairingPhase.Starting;
    private PairingInfo? _pairing;
    private HashSet<string>? _knownClients;
    private ApiException? _error;
    private LiveFeed? _watch;
    private int _remainingSeconds;
    private double _totalSeconds = 1;

    /// <summary>The phases of a pairing.</summary>
    private enum PairingPhase
    {
        Starting = 0,
        Active,
        Expired,
        Ended,
        Paired,
        Failed,
    }

    /// <summary>Raised once when the panel is finished: with <c>true</c> when a client has paired, with <c>false</c> when the user closed it.</summary>
    [Parameter]
    public EventCallback<bool> OnFinished { get; set; }

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private LinkOptions Options { get; set; } = null!;

    [Inject]
    private TimeProvider Time { get; set; } = null!;

    private string PhaseName => _phase.ToString().ToLowerInvariant();

    private double RemainingPercent => Math.Clamp(_remainingSeconds / _totalSeconds * 100, 0, 100);

    private string Countdown => TimeSpan.FromSeconds(Math.Max(0, _remainingSeconds)).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await StartAsync();
    }

    /// <inheritdoc />
    protected override async ValueTask OnDisposeAsync()
    {
        bool cancel = _phase == PairingPhase.Active;
        await StopWatchingAsync();
        if (cancel)
        {
            // Nobody can use the code any more: leaving the dialog withdraws it.
            await TryCancelAsync();
        }
    }

    private async Task StartAsync()
    {
        await StopWatchingAsync();
        _error = null;
        _pairing = null;
        _phase = PairingPhase.Starting;
        try
        {
            PairingInfo info = await Api.StartPairingAsync();
            if (IsDisposed)
            {
                await TryCancelAsync();
                return;
            }

            _knownClients = await ReadClientIdsAsync();
            Apply(info);
            _phase = PairingPhase.Active;
            _watch = new LiveFeed(WatchAsync);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _error = exception;
            _phase = PairingPhase.Failed;
        }
    }

    private void Apply(PairingInfo info)
    {
        if (!string.Equals(_pairing?.Code, info.Code, StringComparison.Ordinal))
        {
            // A new code: the bar is full now and empties until it expires.
            _totalSeconds = Math.Max(1, (info.ExpiresAt - Time.GetUtcNow()).TotalSeconds);
        }

        _pairing = info;
        _remainingSeconds = RemainingSeconds(info);
    }

    private int RemainingSeconds(PairingInfo info)
    {
        return (int)Math.Ceiling(Math.Max(0, (info.ExpiresAt - Time.GetUtcNow()).TotalSeconds));
    }

    /// <summary>The ids of the paired clients, or <c>null</c> when they cannot be read.</summary>
    private async Task<HashSet<string>?> ReadClientIdsAsync()
    {
        try
        {
            LinkClientList clients = await Api.GetLinkClientsAsync();
            return [.. clients.Clients.Select(client => client.Id)];
        }
        catch (ApiException)
        {
            return null;
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Options.PairingPollInterval, Time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!await CheckAsync(cancellationToken))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed or left.
        }
    }

    /// <summary>One step of the watch.</summary>
    /// <returns>True while the code is still being watched.</returns>
    private async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        PairingInfo? shown = _pairing;
        if (shown is null || IsDisposed)
        {
            return false;
        }

        if (RemainingSeconds(shown) <= 0)
        {
            // The agent forgets the code when it expires; the panel does not wait to be told.
            await FinishAsync(PairingPhase.Expired);
            return false;
        }

        PairingInfo? current;
        try
        {
            current = await Api.GetPairingAsync(cancellationToken);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            await InvokeAsync(Session.MarkSignedOut);
            return false;
        }
        catch (ApiException)
        {
            // The agent cannot be reached for the moment; the countdown goes on and the next step asks again.
            current = shown;
        }

        if (current is null)
        {
            // The code is gone before it expired: a client used it, unless it was withdrawn some other way.
            bool paired = await HasNewClientAsync();
            await FinishAsync(paired ? PairingPhase.Paired : PairingPhase.Ended);
            return false;
        }

        await InvokeAsync(() =>
        {
            Apply(current);
            StateHasChanged();
        });
        return true;
    }

    private async Task<bool> HasNewClientAsync()
    {
        HashSet<string>? known = _knownClients;
        HashSet<string>? now = await ReadClientIdsAsync();

        // When the list cannot be compared, a code that was used up is taken for a client that has paired.
        return known is null || now is null || now.Any(id => !known.Contains(id));
    }

    private async Task FinishAsync(PairingPhase phase)
    {
        if (IsDisposed)
        {
            return;
        }

        await InvokeAsync(() =>
        {
            _phase = phase;
            _pairing = null;
            StateHasChanged();
        });
        if (phase == PairingPhase.Paired)
        {
            await InvokeAsync(() => OnFinished.InvokeAsync(true));
        }
    }

    private async Task CloseAsync()
    {
        bool cancel = _phase == PairingPhase.Active;
        await StopWatchingAsync();
        if (cancel)
        {
            await TryCancelAsync();
            _phase = PairingPhase.Ended;
        }

        await OnFinished.InvokeAsync(false);
    }

    private async Task StopWatchingAsync()
    {
        LiveFeed? watch = _watch;
        _watch = null;
        if (watch is not null)
        {
            await watch.DisposeAsync();
        }
    }

    private async Task TryCancelAsync()
    {
        try
        {
            await Api.CancelPairingAsync();
        }
        catch (ApiException)
        {
            // The code expires by itself after a few minutes.
        }
    }
}
