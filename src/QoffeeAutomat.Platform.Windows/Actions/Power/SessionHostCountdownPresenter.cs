using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Sessions;

namespace QoffeeAutomat.Platform.Windows.Actions.Power;

/// <summary>
/// The <see cref="ICountdownPresenter"/> of the Windows agent (PLAN.md S11, T715): the dialog is shown by the SessionHost of the
/// user who is logged in. The agent hands it the internal action <see cref="PowerActionSpec.Countdown"/> as an ordinary <c>run</c>
/// of kind <c>builtin</c> on the session pipe, so no new message is needed: the run lives as long as the dialog is open, the
/// agent's <c>cancel</c> closes the dialog, and the result says what the user did. A run that ends with the error
/// <see cref="ErrorCodes.Cancelled"/> while the agent did not cancel it is the press of the cancel button.
/// </summary>
public sealed class SessionHostCountdownPresenter : ICountdownPresenter
{
    /// <summary>How long after the countdown the run may still last before the pipe gives up on it.</summary>
    public static readonly TimeSpan RunGrace = TimeSpan.FromSeconds(60);

    private readonly WindowsSessionBridge _bridge;

    /// <summary>Creates the presenter.</summary>
    /// <param name="bridge">Reaches the SessionHost of the active console session.</param>
    public SessionHostCountdownPresenter(WindowsSessionBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);

        _bridge = bridge;
    }

    /// <inheritdoc />
    public async ValueTask<CountdownDialogResult> ShowAsync(CountdownRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_bridge.IsReady)
        {
            return CountdownDialogResult.Unavailable;
        }

        var variables = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [PowerActionSpec.CountdownTitle] = request.Title,
            [PowerActionSpec.CountdownSeconds] = (long)request.Seconds,
        };
        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            variables[PowerActionSpec.Message] = request.Message;
        }

        ExecutionResult result = await _bridge.RunBuiltinAsync(
            PowerActionSpec.Countdown,
            variables,
            TimeSpan.FromSeconds(request.Seconds) + RunGrace,
            cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return CountdownDialogResult.Closed;
        }

        if (result.ErrorCode == ErrorCodes.Cancelled)
        {
            return CountdownDialogResult.Cancelled;
        }

        // A dialog whose time was over on its side ends with success; anything else (no session, an error in the session helper)
        // means that no dialog could be shown to the user.
        return result.Succeeded ? CountdownDialogResult.Closed : CountdownDialogResult.Unavailable;
    }
}
