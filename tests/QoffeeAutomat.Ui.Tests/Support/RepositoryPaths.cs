namespace QoffeeAutomat.Ui.Tests.Support;

/// <summary>Finds files of the repository from the test binaries.</summary>
internal static class RepositoryPaths
{
    private static readonly Lazy<string> RootDirectory = new(FindRoot);

    public static string Root => RootDirectory.Value;

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "QoffeeAutomat.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("QoffeeAutomat.slnx was not found above " + AppContext.BaseDirectory + ".");
    }
}
