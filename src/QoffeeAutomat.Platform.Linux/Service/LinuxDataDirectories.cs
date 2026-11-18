using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>The directories of PLAN.md S05 ("Runtime data directories") on Linux.</summary>
/// <param name="Config">Configuration documents, <c>/etc/qoffeeautomat</c> by default.</param>
/// <param name="Scripts">Script files, <c>/etc/qoffeeautomat/scripts</c> by default.</param>
/// <param name="Secrets">Secrets, the setup token and the local channel file, <c>/var/lib/qoffeeautomat/secrets</c> by default.</param>
/// <param name="Data">The SQLite databases and the agent id, <c>/var/lib/qoffeeautomat</c> by default.</param>
/// <param name="Logs">Log files, <c>/var/log/qoffeeautomat</c> by default.</param>
/// <param name="Root">
/// The directory that holds all the others when the layout was chosen with <c>--data-dir</c>; null for the default layout, whose
/// directories lie below <c>/etc</c>, <c>/var/lib</c> and <c>/var/log</c> and have no common parent that the agent may protect.
/// </param>
public sealed record LinuxDataDirectories(string Config, string Scripts, string Secrets, string Data, string Logs, string? Root = null);

/// <summary>What a directory is for.</summary>
public enum LinuxDirectoryRole
{
    /// <summary>The parent of the others, present only for a layout below one directory.</summary>
    Root,

    /// <summary>Configuration documents.</summary>
    Config,

    /// <summary>Script files; they run as root.</summary>
    Scripts,

    /// <summary>Secrets.</summary>
    Secrets,

    /// <summary>Databases.</summary>
    Data,

    /// <summary>Log files.</summary>
    Logs,
}

/// <summary>The permissions that one directory must have.</summary>
/// <param name="Path">The directory.</param>
/// <param name="Role">What it is for.</param>
/// <param name="Mode">The mode that <see cref="LinuxDataDirectoryProvisioner.Provision"/> sets.</param>
public sealed record LinuxDirectoryPolicy(string Path, LinuxDirectoryRole Role, UnixFileMode Mode);

/// <summary>A deviation from the policy found by <see cref="LinuxDataDirectoryProvisioner.Verify"/>.</summary>
/// <param name="Path">The directory or file.</param>
/// <param name="Message">What is wrong and who is affected.</param>
public sealed record LinuxPermissionProblem(string Path, string Message);

/// <summary>The data directories are not protected as PLAN.md S05 and S09 demand; the message says how to fix that.</summary>
public sealed class LinuxDataDirectorySecurityException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public LinuxDataDirectorySecurityException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What is wrong and what to do about it.</param>
    public LinuxDataDirectorySecurityException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What is wrong and what to do about it.</param>
    /// <param name="innerException">The cause.</param>
    public LinuxDataDirectorySecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The file system operations of the provisioner that need more than the framework offers; tests replace it.</summary>
public interface IUnixFileSystem
{
    /// <summary>Tells whether a directory exists.</summary>
    /// <param name="path">The directory.</param>
    /// <returns>True when it exists.</returns>
    bool DirectoryExists(string path);

    /// <summary>Creates a directory, and the missing parents of it, with a mode that is in force from the first moment.</summary>
    /// <param name="path">The directory.</param>
    /// <param name="mode">The mode of the new directory.</param>
    void CreateDirectory(string path, UnixFileMode mode);

    /// <summary>Reads the mode of a file or directory.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The permission bits (and the set-id and sticky bits).</returns>
    UnixFileMode GetMode(string path);

    /// <summary>Sets the mode of a file or directory.</summary>
    /// <param name="path">The path.</param>
    /// <param name="mode">The permission bits.</param>
    void SetMode(string path, UnixFileMode mode);

    /// <summary>Lists the files directly in a directory.</summary>
    /// <param name="path">The directory.</param>
    /// <returns>The full paths of the files, not of directories; empty when the directory does not exist.</returns>
    IReadOnlyList<string> ListFiles(string path);

    /// <summary>Reads the owner of a file or directory.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The numeric user id of the owner; null when it cannot be determined.</returns>
    uint? GetOwner(string path);

    /// <summary>Makes <c>root</c> the owner and group of a file or directory.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True when the owner was set.</returns>
    bool SetOwnerToRoot(string path);
}

/// <summary>
/// The <see cref="IUnixFileSystem"/> of the real computer. The framework reads and sets modes; the owner of a path it cannot tell, so
/// <c>stat</c> and <c>chown</c> are run as programs without a shell (an argument is never interpreted). Both are part of coreutils and busybox.
/// </summary>
public sealed class UnixFileSystem : IUnixFileSystem
{
    private static readonly TimeSpan ProgramTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Unix file modes are not supported on Windows.");
        }

        // The mode applies to the directories that this call creates, minus the umask; the provisioner sets it again.
        Directory.CreateDirectory(path, mode);
    }

    /// <inheritdoc />
    public UnixFileMode GetMode(string path) =>
        OperatingSystem.IsWindows()
            ? throw new PlatformNotSupportedException("Unix file modes are not supported on Windows.")
            : File.GetUnixFileMode(path);

    /// <inheritdoc />
    public void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Unix file modes are not supported on Windows.");
        }

        File.SetUnixFileMode(path, mode);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListFiles(string path)
    {
        return Directory.Exists(path) ? [.. Directory.EnumerateFiles(path).Order(StringComparer.Ordinal)] : [];
    }

    /// <inheritdoc />
    public uint? GetOwner(string path)
    {
        string? output = RunProgram("stat", ["-c", "%u", "--", path]);
        return uint.TryParse(output?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint owner) ? owner : null;
    }

    /// <inheritdoc />
    public bool SetOwnerToRoot(string path) => RunProgram("chown", ["0:0", "--", path]) is not null;

    private static string? RunProgram(string fileName, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            string output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(ProgramTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            // The program is not installed: the caller treats the answer as unknown.
            return null;
        }
    }
}

/// <summary>
/// Creates the data directories with the permissions of PLAN.md S05 and S09 and checks them again at every start. Commands of
/// <c>runAs: system</c> run scripts from these directories as root, and the secrets are in them, so every directory is mode 0700, the
/// files in the secrets directory are mode 0600, and when the agent runs as root everything belongs to root. (Scripts for commands
/// of <c>runAs: user</c> are made readable for the user's session by the session helper, T803.)
/// </summary>
public sealed class LinuxDataDirectoryProvisioner
{
    /// <summary>The mode of every data directory: only the owner has access.</summary>
    public const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The mode of a file in the secrets directory: only the owner can read and write it.</summary>
    public const UnixFileMode SecretFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode GroupAndOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly IUnixFileSystem _files;

    /// <summary>Creates the provisioner.</summary>
    /// <param name="files">Reads and sets modes and owners.</param>
    public LinuxDataDirectoryProvisioner(IUnixFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
    }

    /// <summary>The policy for every directory of a layout, the root (when there is one) first.</summary>
    /// <param name="layout">The directories.</param>
    /// <returns>One policy per directory.</returns>
    public static IReadOnlyList<LinuxDirectoryPolicy> PolicyFor(LinuxDataDirectories layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var policies = new List<LinuxDirectoryPolicy>();
        if (layout.Root is not null)
        {
            policies.Add(new LinuxDirectoryPolicy(layout.Root, LinuxDirectoryRole.Root, DirectoryMode));
        }

        policies.Add(new LinuxDirectoryPolicy(layout.Config, LinuxDirectoryRole.Config, DirectoryMode));
        policies.Add(new LinuxDirectoryPolicy(layout.Scripts, LinuxDirectoryRole.Scripts, DirectoryMode));
        policies.Add(new LinuxDirectoryPolicy(layout.Data, LinuxDirectoryRole.Data, DirectoryMode));
        policies.Add(new LinuxDirectoryPolicy(layout.Secrets, LinuxDirectoryRole.Secrets, DirectoryMode));
        policies.Add(new LinuxDirectoryPolicy(layout.Logs, LinuxDirectoryRole.Logs, DirectoryMode));
        return policies;
    }

    /// <summary>
    /// Creates every directory that is missing and sets the mode of every directory and of the files in the secrets directory,
    /// including those that exist. With <paramref name="makeOwnedByRoot"/> the directories and files also change their owner to
    /// root. This is what <c>service install</c> does; it needs root.
    /// </summary>
    /// <param name="layout">The directories.</param>
    /// <param name="makeOwnedByRoot">True when the caller is root and the data belongs to root.</param>
    /// <exception cref="LinuxDataDirectorySecurityException">The root of the layout is the root of the file system, which must never be re-protected.</exception>
    public void Provision(LinuxDataDirectories layout, bool makeOwnedByRoot)
    {
        ArgumentNullException.ThrowIfNull(layout);
        RejectFileSystemRoot(layout);

        foreach (LinuxDirectoryPolicy policy in PolicyFor(layout))
        {
            if (!_files.DirectoryExists(policy.Path))
            {
                _files.CreateDirectory(policy.Path, policy.Mode);
            }

            _files.SetMode(policy.Path, policy.Mode);
            if (makeOwnedByRoot && !_files.SetOwnerToRoot(policy.Path))
            {
                throw new LinuxDataDirectorySecurityException($"{policy.Path}: the owner could not be set to root.");
            }
        }

        foreach (string file in _files.ListFiles(layout.Secrets))
        {
            _files.SetMode(file, SecretFileMode);
            if (makeOwnedByRoot && !_files.SetOwnerToRoot(file))
            {
                throw new LinuxDataDirectorySecurityException($"{file}: the owner could not be set to root.");
            }
        }
    }

    /// <summary>Checks every directory and the files in the secrets directory against the policy.</summary>
    /// <param name="layout">The directories.</param>
    /// <param name="requireRootOwner">True when the agent runs as root: every directory must then belong to root, or its owner could change what root runs.</param>
    /// <returns>The deviations; empty when everything is as it must be.</returns>
    public IReadOnlyList<LinuxPermissionProblem> Verify(LinuxDataDirectories layout, bool requireRootOwner)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var problems = new List<LinuxPermissionProblem>();
        foreach (LinuxDirectoryPolicy policy in PolicyFor(layout))
        {
            if (!_files.DirectoryExists(policy.Path))
            {
                problems.Add(new LinuxPermissionProblem(policy.Path, "The directory does not exist."));
                continue;
            }

            CheckEntry(policy.Path, requireRootOwner, problems);
        }

        foreach (string file in _files.ListFiles(layout.Secrets))
        {
            CheckEntry(file, requireRootOwner, problems);
        }

        return problems;
    }

    /// <summary>
    /// Makes sure that the directories exist and are protected, as the agent does at every start: directories that are missing are
    /// created with mode 0700 (an existing directory is never re-protected here, only checked), then everything is verified.
    /// </summary>
    /// <param name="layout">The directories.</param>
    /// <param name="requireRootOwner">True when the agent runs as root.</param>
    /// <exception cref="LinuxDataDirectorySecurityException">A directory or secret file is too open or belongs to someone else.</exception>
    public void EnsureSecure(LinuxDataDirectories layout, bool requireRootOwner)
    {
        ArgumentNullException.ThrowIfNull(layout);
        RejectFileSystemRoot(layout);

        foreach (LinuxDirectoryPolicy policy in PolicyFor(layout))
        {
            if (!_files.DirectoryExists(policy.Path))
            {
                _files.CreateDirectory(policy.Path, policy.Mode);

                // The creation mode is reduced by the umask and never grants more; a directory that is not 0700 yet is set right once.
                _files.SetMode(policy.Path, policy.Mode);
            }
        }

        IReadOnlyList<LinuxPermissionProblem> problems = Verify(layout, requireRootOwner);
        if (problems.Count > 0)
        {
            throw new LinuxDataDirectorySecurityException(FormatProblems(problems, requireRootOwner));
        }
    }

    private static string FormatProblems(IReadOnlyList<LinuxPermissionProblem> problems, bool requireRootOwner)
    {
        string remedy = requireRootOwner
            ? "Run 'sudo qoffeeautomat service install' again to repair the permissions, or choose another data directory."
            : "Fix the permissions (chmod 700) or choose another data directory.";
        string details = string.Join(" ", problems.Select(problem => problem.Path + ": " + problem.Message));
        return "The data directories are not protected as required (they hold secrets and scripts that run as the agent's user). " + details + " " + remedy;
    }

    private static void RejectFileSystemRoot(LinuxDataDirectories layout)
    {
        if (layout.Root is { } root && string.Equals(Path.GetFullPath(root), "/", StringComparison.Ordinal))
        {
            throw new LinuxDataDirectorySecurityException("The data directory must not be the root of the file system; its permissions would be replaced.");
        }
    }

    private void CheckEntry(string path, bool requireRootOwner, List<LinuxPermissionProblem> problems)
    {
        UnixFileMode mode = _files.GetMode(path);
        if ((mode & GroupAndOther) != 0)
        {
            problems.Add(new LinuxPermissionProblem(path, $"Its mode is {FormatMode(mode)}, so users other than the owner can access it; only the owner may."));
        }

        if (requireRootOwner && _files.GetOwner(path) is { } owner && owner != 0)
        {
            problems.Add(new LinuxPermissionProblem(path, $"It belongs to the user with id {owner.ToString(CultureInfo.InvariantCulture)}, not to root, so that user can change what the agent runs as root."));
        }
    }

    private static string FormatMode(UnixFileMode mode) => Convert.ToString((int)mode & 0xFFF, 8).PadLeft(4, '0');
}
