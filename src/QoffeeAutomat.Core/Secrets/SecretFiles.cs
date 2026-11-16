namespace QoffeeAutomat.Core.Secrets;

/// <summary>File system helpers shared by the secret classes.</summary>
internal static class SecretFiles
{
    /// <summary>Creates a directory that only its owner can enter (mode 0700 on Unix). An existing directory is left as it is.</summary>
    public static void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
