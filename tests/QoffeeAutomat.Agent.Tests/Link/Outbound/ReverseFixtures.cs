namespace QoffeeAutomat.Agent.Tests.Link.Outbound;

/// <summary>The fixtures of the reverse link in <c>protocol/fixtures/reverse</c> and the QoffeeLink frames in <c>protocol/fixtures</c> that they refer to.</summary>
internal static class ReverseFixtures
{
    /// <summary>Reads a file of <c>protocol/fixtures/reverse</c>.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The text.</returns>
    public static string ReadReverse(string name) => File.ReadAllText(Locate("fixtures", "reverse", name));

    /// <summary>Reads a file of <c>protocol/fixtures</c>.</summary>
    /// <param name="name">The file name, for example <c>client_hello.json</c>.</param>
    /// <returns>The text.</returns>
    public static string ReadFrame(string name) => File.ReadAllText(Locate("fixtures", name));

    private static string Locate(params string[] relative)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine([directory, "protocol", .. relative]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException("The fixture protocol/" + string.Join('/', relative) + " was not found.");
    }
}
