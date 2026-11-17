using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.Core.Platform;

/// <summary>
/// Reports what became of the global hotkeys of the settings (PLAN.md S16, T708): which are registered and which conflict with
/// another program or with each other. The Windows platform implements it from the answers of the SessionHost; on other platforms
/// <see cref="NoHotkeyStatusSource"/> says that hotkeys are not supported. The management API shows the result so that the UI can
/// report registration conflicts.
/// </summary>
public interface IHotkeyStatusSource
{
    /// <summary>Whether this platform registers global hotkeys at all.</summary>
    bool IsSupported { get; }

    /// <summary>The status of the hotkeys of the last list that a session helper acknowledged; empty when none did yet.</summary>
    /// <returns>One entry per hotkey.</returns>
    IReadOnlyList<SessionHotkeyStatus> GetStatus();
}

/// <summary>The status source of a platform without global hotkeys.</summary>
public sealed class NoHotkeyStatusSource : IHotkeyStatusSource
{
    /// <summary>The only instance.</summary>
    public static NoHotkeyStatusSource Instance { get; } = new();

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public IReadOnlyList<SessionHotkeyStatus> GetStatus() => [];
}
