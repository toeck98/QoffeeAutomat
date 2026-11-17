using Microsoft.Extensions.Hosting;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// Restarts the agent on request of the management API (PLAN.md S08, <c>POST /agent/restart</c>). The abstraction exists so
/// that tests can replace it: a test must never stop the host it runs in.
/// </summary>
public interface IAgentRestarter
{
    /// <summary>Asks for a restart. Returns at once; the restart happens a moment later so that the response can be sent.</summary>
    void RequestRestart();
}

/// <summary>
/// Stops the host gracefully after a short delay. The service manager (or the tray) starts the agent again; run without
/// one, the process ends.
/// </summary>
public sealed class HostAgentRestarter : IAgentRestarter
{
    /// <summary>How long the agent waits before it stops, so that the 202 response reaches the caller.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _time;
    private int _requested;

    /// <summary>Creates the restarter.</summary>
    /// <param name="lifetime">The lifetime of the host.</param>
    /// <param name="time">The clock.</param>
    public HostAgentRestarter(IHostApplicationLifetime lifetime, TimeProvider time)
    {
        _lifetime = lifetime;
        _time = time;
    }

    /// <inheritdoc />
    public void RequestRestart()
    {
        // A second request while the first is pending changes nothing.
        if (Interlocked.Exchange(ref _requested, 1) != 0)
        {
            return;
        }

        _ = StopLaterAsync();
    }

    private async Task StopLaterAsync()
    {
        await Task.Delay(Delay, _time).ConfigureAwait(false);
        _lifetime.StopApplication();
    }
}
