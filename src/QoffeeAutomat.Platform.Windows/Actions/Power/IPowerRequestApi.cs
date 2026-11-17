namespace QoffeeAutomat.Platform.Windows.Actions.Power;

/// <summary>
/// The Windows calls behind <c>power.keep_awake</c> (PLAN.md S11, T715): the seam that lets the tests cover the keep-awake logic
/// without ever holding the computer awake. The real implementation is <see cref="WindowsPowerRequestApi"/>: a power request
/// that the agent process owns, so Windows ends it when the process ends, whatever the reason.
/// </summary>
public interface IPowerRequestApi
{
    /// <summary>
    /// Keeps the computer from going to sleep by idle timeout; with <paramref name="keepDisplayOn"/> the displays stay on as well.
    /// Calling it again changes the display part and leaves the rest as it was.
    /// </summary>
    /// <param name="keepDisplayOn">Also keep the displays on.</param>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows refused the request; nothing is held then.</exception>
    void Acquire(bool keepDisplayOn);

    /// <summary>Gives the request back; nothing happens when none is held. It never throws.</summary>
    void Release();
}
