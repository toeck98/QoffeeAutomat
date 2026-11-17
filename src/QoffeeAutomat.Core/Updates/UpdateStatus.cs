namespace QoffeeAutomat.Core.Updates;

/// <summary>
/// What the agent knows about its own version and the newest release (PLAN.md S13, S16, T704). The metrics
/// <c>agent.version</c>, <c>agent.latest_version</c>, <c>agent.release_url</c> and <c>agent.release_notes</c> report it.
/// </summary>
/// <param name="InstalledVersion">The version of the running agent, for example <c>1.2.0</c>.</param>
/// <param name="LatestVersion">The newest published release; null while it is not known (the check is off, has not run yet or failed).</param>
/// <param name="ReleaseUrl">The address of the release page of <paramref name="LatestVersion"/>.</param>
/// <param name="ReleaseNotes">The release notes of <paramref name="LatestVersion"/> (Markdown, shortened to <see cref="UpdateStatus.MaxNotesLength"/> characters).</param>
/// <param name="CheckedAt">When the release was looked up last.</param>
public sealed record UpdateStatus(
    string InstalledVersion,
    string? LatestVersion = null,
    string? ReleaseUrl = null,
    string? ReleaseNotes = null,
    DateTimeOffset? CheckedAt = null)
{
    /// <summary>The longest release notes that are kept and published.</summary>
    public const int MaxNotesLength = 4000;
}

/// <summary>
/// The source of the <see cref="UpdateStatus"/>. The metric provider reads it and the agent host asks the metric engine for a fresh
/// sample whenever <see cref="Changed"/> is raised, so Home Assistant learns about a release without polling.
/// </summary>
public interface IUpdateStatusSource
{
    /// <summary>The current status; never null.</summary>
    UpdateStatus Current { get; }

    /// <summary>Raised after <see cref="Current"/> changed. Handlers return quickly.</summary>
    event EventHandler? Changed;
}

/// <summary>An <see cref="IUpdateStatusSource"/> that never changes: the installed version only.</summary>
public sealed class StaticUpdateStatusSource : IUpdateStatusSource
{
    /// <summary>Creates the source.</summary>
    /// <param name="installedVersion">The version of the running agent.</param>
    public StaticUpdateStatusSource(string installedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedVersion);

        Current = new UpdateStatus(installedVersion);
    }

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public UpdateStatus Current { get; }
}
