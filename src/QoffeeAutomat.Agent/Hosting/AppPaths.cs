namespace QoffeeAutomat.Agent.Hosting;

/// <summary>The operating system family the agent runs on.</summary>
public enum HostOs
{
    /// <summary>Windows.</summary>
    Windows,

    /// <summary>Linux.</summary>
    Linux,

    /// <summary>Anything else (macOS, BSD); only the simulated platform runs there.</summary>
    Other,
}

/// <summary>Where the agent keeps its files (PLAN.md S05, "Runtime data directories").</summary>
public interface IAppPaths
{
    /// <summary>Configuration documents: <c>commands/</c>, <c>sensors/</c>, <c>metrics.json</c>, <c>settings.json</c>.</summary>
    string ConfigDirectory { get; }

    /// <summary>Script files. Scripts run as LocalSystem, so the directory must be writable by administrators only (S09).</summary>
    string ScriptsDirectory { get; }

    /// <summary>The secrets file and its key.</summary>
    string SecretsDirectory { get; }

    /// <summary>Data such as the SQLite databases.</summary>
    string DataDirectory { get; }

    /// <summary>Log files.</summary>
    string LogsDirectory { get; }
}

/// <summary>
/// The default <see cref="IAppPaths"/>. The defaults per operating system are those of PLAN.md S05;
/// <c>--data-dir</c> and the environment variable <see cref="DataDirEnvironmentVariable"/> move
/// everything below one directory.
/// </summary>
/// <param name="ConfigDirectory">The configuration directory.</param>
/// <param name="ScriptsDirectory">The scripts directory.</param>
/// <param name="SecretsDirectory">The secrets directory.</param>
/// <param name="DataDirectory">The data directory.</param>
/// <param name="LogsDirectory">The logs directory.</param>
public sealed record AppPaths(
    string ConfigDirectory,
    string ScriptsDirectory,
    string SecretsDirectory,
    string DataDirectory,
    string LogsDirectory) : IAppPaths
{
    /// <summary>The environment variable that overrides the data directory.</summary>
    public const string DataDirEnvironmentVariable = "QOFFEEAUTOMAT_DATA_DIR";

    /// <summary>The name of the directory below ProgramData (Windows) or the user's data folder (other systems).</summary>
    public const string ProductDirectoryName = "QoffeeAutomat";

    /// <summary>Lays out everything below one directory: <c>config</c>, <c>scripts</c>, <c>secrets</c>, <c>data</c>, <c>logs</c>.</summary>
    /// <param name="root">The root directory; a relative path is made absolute.</param>
    /// <returns>The paths.</returns>
    public static AppPaths ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string full = Path.GetFullPath(root);
        return new AppPaths(
            Path.Combine(full, "config"),
            Path.Combine(full, "scripts"),
            Path.Combine(full, "secrets"),
            Path.Combine(full, "data"),
            Path.Combine(full, "logs"));
    }

    /// <summary>The Linux layout of PLAN.md S05 (used from milestone M8; defined now so it is tested).</summary>
    /// <returns>The paths below <c>/etc/qoffeeautomat</c>, <c>/var/lib/qoffeeautomat</c> and <c>/var/log/qoffeeautomat</c>.</returns>
    public static AppPaths ForLinuxSystem()
    {
        return new AppPaths(
            "/etc/qoffeeautomat",
            "/etc/qoffeeautomat/scripts",
            "/var/lib/qoffeeautomat/secrets",
            "/var/lib/qoffeeautomat",
            "/var/log/qoffeeautomat");
    }

    /// <summary>Resolves the paths: the option wins over the environment variable, which wins over the defaults of the operating system.</summary>
    /// <param name="dataDirOption">The value of <c>--data-dir</c>, or null.</param>
    /// <param name="dataDirEnvironment">The value of <see cref="DataDirEnvironmentVariable"/>, or null.</param>
    /// <param name="os">The operating system family.</param>
    /// <param name="programDataFolder">The ProgramData folder on Windows, or the folder for per-user application data elsewhere.</param>
    /// <returns>The paths.</returns>
    public static AppPaths Resolve(string? dataDirOption, string? dataDirEnvironment, HostOs os, string programDataFolder)
    {
        if (!string.IsNullOrWhiteSpace(dataDirOption))
        {
            return ForRoot(dataDirOption);
        }

        if (!string.IsNullOrWhiteSpace(dataDirEnvironment))
        {
            return ForRoot(dataDirEnvironment);
        }

        return os == HostOs.Linux
            ? ForLinuxSystem()
            : ForRoot(Path.Combine(programDataFolder, ProductDirectoryName));
    }

    /// <summary>Resolves the paths for the running process.</summary>
    /// <param name="dataDirOption">The value of <c>--data-dir</c>, or null.</param>
    /// <returns>The paths.</returns>
    public static AppPaths ForCurrentProcess(string? dataDirOption)
    {
        HostOs os = OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsLinux() ? HostOs.Linux : HostOs.Other;
        Environment.SpecialFolder folder = os == HostOs.Windows
            ? Environment.SpecialFolder.CommonApplicationData
            : Environment.SpecialFolder.LocalApplicationData;
        return Resolve(
            dataDirOption,
            Environment.GetEnvironmentVariable(DataDirEnvironmentVariable),
            os,
            Environment.GetFolderPath(folder));
    }
}
