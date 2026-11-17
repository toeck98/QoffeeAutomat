using System.Runtime.InteropServices;

namespace QoffeeAutomat.Agent.Updates;

/// <summary>
/// The fixed parameters of the update check and of the self-update (PLAN.md S09 item 13, S13, T704). A test replaces the registration
/// to point the agent at a fake server, to run without the waiting times and to switch the self-update on for the simulated platform.
/// The user's choices (check on or off, how often) live in the settings document, <see cref="QoffeeAutomat.Contracts.Config.UpdateSettings"/>.
/// </summary>
public sealed class UpdateOptions
{
    /// <summary>The name of the <c>HttpClient</c> that the check and the download use.</summary>
    public const string HttpClientName = "qoffeeautomat-updates";

    /// <summary>The release asset that lists the SHA-256 checksum of every other asset (PLAN.md S13).</summary>
    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    /// <summary>
    /// Whether this agent can update itself: the platform can run the installer (the Windows platform). Without it the agent neither asks
    /// GitHub nor offers <c>agent.update</c>; it only reports its own version.
    /// </summary>
    public bool Supported { get; init; }

    /// <summary>The GitHub repository that publishes the releases, <c>owner/name</c>.</summary>
    public string Repository { get; init; } = "toeck98/QoffeeAutomat";

    /// <summary>The address of the GitHub API.</summary>
    public Uri ApiBaseAddress { get; init; } = new("https://api.github.com/");

    /// <summary>The architecture suffix of the installer, <c>x64</c> or <c>arm64</c>.</summary>
    public string InstallerArchitecture { get; init; } = DefaultArchitecture();

    /// <summary>The version of the running agent.</summary>
    public string InstalledVersion { get; init; } = QoffeeAutomat.Agent.Hosting.AgentVersion.Current;

    /// <summary>The executable of the running agent; its Authenticode signature decides whether the installer must be signed too.</summary>
    public string? RunningExecutablePath { get; init; } = Environment.ProcessPath;

    /// <summary>How long after the start of the agent the first check waits, so that the start is not slowed down.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the checker looks at its settings while it waits (a change of the settings takes effect after this time at the latest). Looking costs nothing; it is no network request.</summary>
    public TimeSpan SettingsPollInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The time limit of a request to the releases API.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The time limit of the download of one file.</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The largest answer of the releases API that is read, in bytes.</summary>
    public int MaxReleaseBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>The largest checksum file that is read, in bytes.</summary>
    public int MaxChecksumBytes { get; init; } = 1024 * 1024;

    /// <summary>The largest installer that is downloaded, in bytes.</summary>
    public long MaxInstallerBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>How long the installer may take; it is started and left running, so this only bounds the start.</summary>
    public TimeSpan InstallerStartTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The architecture of this process as the installer names it.</summary>
    /// <returns><c>arm64</c> on an ARM64 process, otherwise <c>x64</c>.</returns>
    public static string DefaultArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    }
}
