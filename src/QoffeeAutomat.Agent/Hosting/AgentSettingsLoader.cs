using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// Reads <c>config/settings.json</c> synchronously for the few things that are needed before the
/// config store exists: the ports for Kestrel and the log level. Kestrel's configuration callback is
/// synchronous, so this reads the small file directly. A missing, unreadable or invalid file yields
/// the defaults and a message; the config store later reports the same file as a problem.
/// </summary>
public static class AgentSettingsLoader
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Reads the settings.</summary>
    /// <param name="paths">Where the config directory is.</param>
    /// <param name="problem">Why the file was not used; null when it was read or does not exist.</param>
    /// <returns>The settings, or the defaults with the host name as agent name.</returns>
    public static AgentSettingsDocument LoadOrDefault(IAppPaths paths, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(paths);

        problem = null;
        string file = Path.Combine(paths.ConfigDirectory, ConfigFileNames.Settings);
        if (!File.Exists(file))
        {
            return Default();
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(file);
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith(new ReadOnlySpan<byte>(Utf8Bom)))
            {
                json = json[Utf8Bom.Length..];
            }

            AgentSettingsDocument? document = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AgentSettingsDocument);
            if (document is null)
            {
                problem = "settings.json is empty; the defaults are used.";
                return Default();
            }

            ValidationResult validation = DocumentValidator.ValidateSettings(document);
            if (!validation.IsValid)
            {
                ValidationError first = validation.Errors[0];
                problem = $"settings.json is invalid ({first.Path}: {first.Message}); the defaults are used.";
                return Default();
            }

            return document;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = "settings.json cannot be read (" + exception.GetType().Name + "); the defaults are used.";
            return Default();
        }
    }

    private static AgentSettingsDocument Default()
    {
        return new AgentSettingsDocument(DocumentSchema.CurrentVersion, Environment.MachineName);
    }
}

/// <summary>The names of well-known files in the config directory.</summary>
public static class ConfigFileNames
{
    /// <summary>The settings document.</summary>
    public const string Settings = "settings.json";
}
