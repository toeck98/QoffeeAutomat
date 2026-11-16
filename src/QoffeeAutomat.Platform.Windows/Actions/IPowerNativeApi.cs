namespace QoffeeAutomat.Platform.Windows.Actions;

/// <summary>
/// The Windows calls behind the power actions (PLAN.md S11, T306): the seam that lets the tests cover shutdown, restart,
/// sleep and hibernate without ever touching the computer that runs them. The real implementation is
/// <see cref="WindowsPowerNativeApi"/>. Methods that fail throw <see cref="System.ComponentModel.Win32Exception"/>; they
/// return when the request was made, not when the computer has changed state.
/// </summary>
public interface IPowerNativeApi
{
    /// <summary>Tells whether the computer can sleep (the firmware and the power policy allow it).</summary>
    bool CanSleep { get; }

    /// <summary>Tells whether hibernation is enabled. Without it Windows would sleep instead of hibernating.</summary>
    bool CanHibernate { get; }

    /// <summary>Shuts the computer down and switches the power off.</summary>
    /// <param name="force">Close programs that do not respond instead of waiting for them.</param>
    void Shutdown(bool force);

    /// <summary>Restarts the computer.</summary>
    /// <param name="force">Close programs that do not respond instead of waiting for them.</param>
    void Restart(bool force);

    /// <summary>Puts the computer to sleep.</summary>
    void Sleep();

    /// <summary>Hibernates the computer.</summary>
    void Hibernate();
}
