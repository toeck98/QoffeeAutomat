using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>
/// The data directory of the hub, created on first use with owner-only permissions (PLAN.md S09, S20, T902): directories get
/// mode 0700 and files that hold secrets 0600 on Linux. On Windows the permissions are those of the parent directory (the hub
/// ships as a container image and has no Windows package). An existing directory that group or others can enter is tightened; when
/// that fails (a volume that belongs to another user) the hub starts and logs a warning.
/// </summary>
public sealed class HubDataDirectory
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode ForbiddenMode =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Creates the data directory and the secrets directory.</summary>
    /// <param name="settings">The settings with the paths.</param>
    /// <param name="logger">The logger.</param>
    public HubDataDirectory(HubSettings settings, ILogger<HubDataDirectory> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        Paths = settings.Paths;
        if (!EnsurePrivateDirectory(Paths.Root))
        {
            HubLog.DirectoryNotPrivate(logger, Paths.Root);
        }

        if (!EnsurePrivateDirectory(Paths.SecretsDirectory))
        {
            HubLog.DirectoryNotPrivate(logger, Paths.SecretsDirectory);
        }
    }

    /// <summary>The paths of the hub files.</summary>
    public HubPaths Paths { get; }

    /// <summary>Creates a directory that only its owner can enter, or restricts an existing one.</summary>
    /// <param name="path">The directory.</param>
    /// <returns>True when the directory is private (always true on Windows); false when it could not be restricted.</returns>
    public static bool EnsurePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return true;
        }

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
            return true;
        }

        return Restrict(path, PrivateDirectoryMode);
    }

    /// <summary>Creates an empty file that only its owner can read, when it does not exist, or restricts an existing one.</summary>
    /// <param name="path">The file.</param>
    /// <returns>True when the file is private (always true on Windows); false when it could not be restricted.</returns>
    public static bool EnsurePrivateFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (OperatingSystem.IsWindows())
        {
            if (!File.Exists(path))
            {
                using FileStream created = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }

            return true;
        }

        if (!File.Exists(path))
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = PrivateFileMode,
            };

            using FileStream created = new(path, options);
            return true;
        }

        return Restrict(path, PrivateFileMode);
    }

    private static bool Restrict(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            if ((File.GetUnixFileMode(path) & ForbiddenMode) == UnixFileMode.None)
            {
                return true;
            }

            File.SetUnixFileMode(path, mode);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
