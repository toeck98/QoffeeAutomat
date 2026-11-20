namespace QoffeeAutomat.Hub.Hosting;

/// <summary>
/// Where the hub keeps its files (PLAN.md S20, T902). Everything lives below one data directory: the database
/// <c>hub.db</c>, the <c>secrets</c> directory with the key file and the protected secrets (TLS certificate, signing key),
/// and the one-time setup token. In the container the directory is <c>/data</c>, a volume.
/// </summary>
/// <param name="Root">The absolute path of the data directory.</param>
public sealed record HubPaths(string Root)
{
    /// <summary>The environment variable that overrides the data directory (the option <c>--data-dir</c> wins over it).</summary>
    public const string DataDirEnvironmentVariable = "QOFFEE_HUB_DATA_DIR";

    /// <summary>The data directory inside the container image.</summary>
    public const string ContainerDataDirectory = "/data";

    /// <summary>The name of the product directory below the user's application data folder when no other directory is configured.</summary>
    public const string ProductDirectoryName = "QoffeeAutomatHub";

    /// <summary>The name of the database file.</summary>
    public const string DatabaseFileName = "hub.db";

    /// <summary>The name of the secrets directory below the data directory.</summary>
    public const string SecretsDirectoryName = "secrets";

    /// <summary>The name of the key file in the secrets directory.</summary>
    public const string SecretKeyFileName = "secrets.key";

    /// <summary>The name of the file of protected secrets in the secrets directory.</summary>
    public const string SecretsFileName = "secrets.json";

    /// <summary>The name of the file that holds the one-time setup token while no administrator exists.</summary>
    public const string SetupTokenFileName = "setup-token.txt";

    /// <summary>The SQLite database.</summary>
    public string DatabasePath => Path.Combine(Root, DatabaseFileName);

    /// <summary>The directory of the secrets; owner access only.</summary>
    public string SecretsDirectory => Path.Combine(Root, SecretsDirectoryName);

    /// <summary>The key file that protects the secrets at rest.</summary>
    public string SecretKeyPath => Path.Combine(SecretsDirectory, SecretKeyFileName);

    /// <summary>The file of protected secrets.</summary>
    public string SecretsFilePath => Path.Combine(SecretsDirectory, SecretsFileName);

    /// <summary>The one-time setup token file (mode 0600).</summary>
    public string SetupTokenPath => Path.Combine(Root, SetupTokenFileName);

    /// <summary>Lays out the hub files below one directory.</summary>
    /// <param name="root">The data directory; a relative path is made absolute.</param>
    /// <returns>The paths.</returns>
    public static HubPaths ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return new HubPaths(Path.GetFullPath(root));
    }

    /// <summary>Resolves the data directory: the option wins over the environment variable, which wins over the default.</summary>
    /// <param name="dataDirOption">The value of <c>--data-dir</c>, or null.</param>
    /// <param name="dataDirEnvironment">The value of <see cref="DataDirEnvironmentVariable"/>, or null.</param>
    /// <param name="inContainer">True when the hub runs in a container: the default is then <see cref="ContainerDataDirectory"/>.</param>
    /// <param name="localAppData">The user's application data folder, the base of the default outside a container.</param>
    /// <returns>The paths.</returns>
    public static HubPaths Resolve(string? dataDirOption, string? dataDirEnvironment, bool inContainer, string localAppData)
    {
        if (!string.IsNullOrWhiteSpace(dataDirOption))
        {
            return ForRoot(dataDirOption);
        }

        if (!string.IsNullOrWhiteSpace(dataDirEnvironment))
        {
            return ForRoot(dataDirEnvironment);
        }

        return inContainer
            ? ForRoot(ContainerDataDirectory)
            : ForRoot(Path.Combine(localAppData, ProductDirectoryName));
    }
}
