using System.Text.Json;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Core.Tests.Validation;

/// <summary>Fixture loading and small builders on top of <see cref="TestDocuments"/>.</summary>
internal static class TestData
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRoot);

    public static string ConfigFixtureDirectory => Path.Combine(RepositoryRoot.Value, "protocol", "fixtures", "config");

    public static string ConfigFixture(string name) => Path.Combine(ConfigFixtureDirectory, name);

    public static CommandDocument ReadCommand(string fileName)
    {
        string json = File.ReadAllText(ConfigFixture(fileName));
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CommandDocument)
            ?? throw new InvalidOperationException(fileName + " is null.");
    }

    public static ParameterDefinition Choice(string name, params string[] options) =>
        new(name, ParameterTypes.Choice, Options: options);

    public static CommandDocument WithControl(HaControl control, params ParameterDefinition[] parameters) =>
        TestDocuments.Command() with { Parameters = parameters, HaControl = control };

    public static SensorDocument Sensor() => new(
        DocumentSchema.CurrentVersion,
        "cpu_temp",
        "CPU temperature",
        true,
        true,
        "sensor",
        "number",
        new ProcessRunningSensorSource("notepad"));

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
