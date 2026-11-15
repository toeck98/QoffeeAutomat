using System.Text.Json;
using QoffeeAutomat.Contracts.Tests.Schema;

namespace QoffeeAutomat.Contracts.Tests;

/// <summary>Locates the shared protocol folder (schemas and fixtures) of the repository.</summary>
internal static class RepositoryPaths
{
    private static readonly Lazy<SchemaSet> SharedSchemas = new(() => SchemaSet.Load(SchemaDirectory));

    public static string Root { get; } = FindRoot();

    public static string ProtocolDirectory => Path.Combine(Root, "protocol");

    public static string SchemaDirectory => Path.Combine(ProtocolDirectory, "schema");

    public static string FixtureDirectory => Path.Combine(ProtocolDirectory, "fixtures");

    /// <summary>All schemas of protocol/schema, loaded once.</summary>
    public static SchemaSet Schemas => SharedSchemas.Value;

    /// <summary>The base of the schema ids.</summary>
    public static string SchemaId(string relativeName) =>
        "https://github.com/toeck98/QoffeeAutomat/protocol/schema/" + relativeName;

    /// <summary>The fixture files of a folder below protocol/fixtures, as paths relative to the fixture directory.</summary>
    public static IReadOnlyList<string> FixtureFiles(string folder)
    {
        string directory = folder.Length == 0 ? FixtureDirectory : Path.Combine(FixtureDirectory, folder);
        return Directory.EnumerateFiles(directory, "*.json")
            .Select(path => Path.GetRelativePath(FixtureDirectory, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The type of a fixture: the part of the file name before the first dot.</summary>
    public static string FixtureType(string relativePath) =>
        Path.GetFileName(relativePath).Split('.')[0];

    public static string ReadFixture(string relativePath) =>
        File.ReadAllText(Path.Combine(FixtureDirectory, relativePath));

    public static JsonElement ReadFixtureElement(string relativePath)
    {
        using JsonDocument document = JsonDocument.Parse(ReadFixture(relativePath));
        return document.RootElement.Clone();
    }

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "global.json"))
                && Directory.Exists(Path.Combine(directory, "protocol")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("The repository root (global.json and protocol/) was not found.");
    }
}
