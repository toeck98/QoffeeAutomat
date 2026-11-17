using System.ComponentModel;
using System.Windows.Forms;

namespace QoffeeAutomat.SessionHost.Dialogs;

/// <summary>
/// The <see cref="ICountdownDialog"/> of the desktop (PLAN.md S11, T715): a Windows Forms window (<see cref="CountdownForm"/>) on a
/// thread of its own with a message loop, because the SessionHost is asynchronous code without one. Each call opens one window; a
/// failure to show it surfaces as <see cref="InvalidOperationException"/>, which the action reports to the agent, whose countdown then
/// simply runs on without a dialog. This class puts a window on a real desktop; the tests use a fake.
/// </summary>
public sealed class WinFormsCountdownDialog : ICountdownDialog
{
    private readonly ISessionHostLog _log;

    /// <summary>Creates the dialog.</summary>
    /// <param name="log">The diagnostics log.</param>
    public WinFormsCountdownDialog(ISessionHostLog? log = null)
    {
        _log = log ?? NullSessionHostLog.Instance;
    }

    /// <inheritdoc />
    public Task<CountdownDialogOutcome> ShowAsync(CountdownDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(CountdownDialogOutcome.Closed);
        }

        var completion = new TaskCompletionSource<CountdownDialogOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Run(request, completion, cancellationToken))
        {
            Name = "QoffeeAutomat countdown",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private void Run(CountdownDialogRequest request, TaskCompletionSource<CountdownDialogOutcome> completion, CancellationToken cancellationToken)
    {
        // An exception in a handler of the window must not take the SessionHost down: the dialog is a convenience, and the countdown of
        // the agent goes on without it.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
        ThreadExceptionEventHandler onThreadException = (_, e) => _log.Warning("The countdown dialog failed: " + e.Exception.Message);
        Application.ThreadException += onThreadException;
        try
        {
            using var form = new CountdownForm(request);
            using CancellationTokenRegistration registration = cancellationToken.Register(form.Dismiss);
            Application.Run(form);
            completion.TrySetResult(form.Outcome);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or ObjectDisposedException)
        {
            _log.Warning("The countdown dialog could not be shown: " + exception.Message);
            completion.TrySetException(new InvalidOperationException("The countdown dialog could not be shown.", exception));
        }
        finally
        {
            Application.ThreadException -= onThreadException;
        }
    }
}
