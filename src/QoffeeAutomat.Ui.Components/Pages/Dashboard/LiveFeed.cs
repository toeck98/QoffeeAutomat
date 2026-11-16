namespace QoffeeAutomat.Ui.Components.Pages.Dashboard;

/// <summary>
/// A piece of background work of a page that runs until it is disposed: the metric stream and the periodic refresh of
/// the dashboard. Disposing cancels the work and waits until it has finished, so that the stream is really closed
/// (and the agent stops sampling) when the call returns.
/// </summary>
public sealed class LiveFeed : IAsyncDisposable, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _work;
    private bool _disposed;

    /// <summary>Starts the work.</summary>
    /// <param name="run">The work; it must end when the token is cancelled.</param>
    public LiveFeed(Func<CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        _work = run(_cancellation.Token);
    }

    /// <summary>Cancels the work without waiting for it; prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cancellation.CancelAsync();
        try
        {
            await _work;
        }
        catch (OperationCanceledException)
        {
            // The expected end of cancelled work.
        }

        _cancellation.Dispose();
    }
}
