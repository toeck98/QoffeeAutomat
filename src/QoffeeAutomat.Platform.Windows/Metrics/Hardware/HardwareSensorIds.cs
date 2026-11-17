using System.Globalization;
using System.Text;

namespace QoffeeAutomat.Platform.Windows.Metrics.HardwareSensors;

/// <summary>Builds the ids and names of the hardware metrics (PLAN.md S06, T707). Ids are stable: they are part of the Home Assistant unique id.</summary>
public static class HardwareSensorIds
{
    /// <summary>Every hardware metric id starts with this prefix, followed by a dot.</summary>
    public const string Prefix = "hardware";

    /// <summary>
    /// Turns the identifier of a sensor into a metric id: <c>/intelcpu/0/temperature/0</c> becomes <c>hardware.intelcpu.0.temperature.0</c>.
    /// Segments are lower case; every character other than a letter, digit, underscore or hyphen becomes an underscore.
    /// </summary>
    /// <param name="path">The identifier as the library reports it.</param>
    /// <returns>The metric id; it matches the id pattern of PLAN.md S06.</returns>
    public static string FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var id = new StringBuilder(Prefix);
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            id.Append('.');
            int start = id.Length;
            foreach (char character in segment)
            {
                id.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? char.ToLowerInvariant(character) : '_');
            }

            if (id.Length == start)
            {
                id.Append('_');
            }
        }

        return id.ToString();
    }

    /// <summary>
    /// Gives every reading a unique metric id. Ids come from the paths, which the library keeps unique; only two paths that differ in
    /// characters that are replaced would collide, and then the later one gets a numeric suffix.
    /// </summary>
    /// <param name="readings">The readings, in a stable order.</param>
    /// <returns>The ids in the order of <paramref name="readings"/>.</returns>
    public static IReadOnlyList<string> Assign(IReadOnlyList<HardwareSensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>(readings.Count);
        foreach (HardwareSensorReading reading in readings)
        {
            string baseId = FromPath(reading.Path);
            string candidate = baseId;
            for (int number = 2; !used.Add(candidate); number++)
            {
                candidate = string.Create(CultureInfo.InvariantCulture, $"{baseId}_{number}");
            }

            ids.Add(candidate);
        }

        return ids;
    }

    /// <summary>The display name, for example <c>Intel Core i7-12700K CPU Package power</c>. The kind is appended unless the sensor name already says it.</summary>
    /// <param name="reading">The sensor.</param>
    /// <returns>The name.</returns>
    public static string DisplayName(HardwareSensorReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        string word = reading.Kind switch
        {
            HardwareSensorKind.Temperature => "temperature",
            HardwareSensorKind.Fan => "fan speed",
            _ => "power",
        };
        string sensor = reading.SensorName.Trim();
        bool alreadySaid = sensor.Contains(word, StringComparison.OrdinalIgnoreCase)
            || (reading.Kind == HardwareSensorKind.Fan && sensor.Contains("fan", StringComparison.OrdinalIgnoreCase));
        string name = alreadySaid ? sensor : $"{sensor} {word}".Trim();
        string hardware = reading.HardwareName.Trim();
        return hardware.Length == 0 ? name : $"{hardware} {name}";
    }
}
